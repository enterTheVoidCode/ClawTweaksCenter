using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClawTweaksCenter.Audio
{
    /// <summary>The short sounds the library plays. The FILE behind each one is chosen by name, see
    /// <see cref="UiSounds"/> - a missing file is silence, never an error.</summary>
    public enum UiSound
    {
        /// <summary>D-pad, left stick, shoulders.</summary>
        Navigate,
        /// <summary>A.</summary>
        Confirm,
        /// <summary>B.</summary>
        Back,
        /// <summary>A game was started.</summary>
        Launch,
    }

    /// <summary>
    /// Interface sounds and background music for the library, through ONE WASAPI stream and a mixer.
    ///
    /// ── WHERE THE SOUNDS COME FROM ─────────────────────────────────────────────────────────────
    /// By base name, first match wins:
    ///   1. %LOCALAPPDATA%\ClawTweaks\Center\sounds\    - loose files, for trying sounds without a build
    ///   2. Assets\sounds\ embedded in the exe          - what ships
    /// Effects are navigate / confirm / back / launch; every file whose name starts with "music" is a
    /// music track, played in name order and looped. .ogg, .wav and .mp3 are read.
    ///
    /// Both folders are read once, on the first warm-up. Files added while Center runs count from
    /// the next start.
    ///
    /// ── WHY NAudio AND NOT WPF'S MediaPlayer ───────────────────────────────────────────────────
    /// MediaPlayer cannot decode Ogg Vorbis without an optional Windows extension, starts a clip
    /// with a delay you can hear on a D-pad press, and every overlapping sound needs its own player.
    /// Here every effect is decoded ONCE into the mixer's format and a press is an array copy on the
    /// audio thread.
    ///
    /// ── WHY THE STREAM PAUSES ITSELF ───────────────────────────────────────────────────────────
    /// A running WASAPI stream renders silence forever and keeps the audio endpoint awake, on a
    /// battery device, for a library that is mostly looked at. The stream is paused a few seconds
    /// after the last sound ends and resumed on the next one.
    ///
    /// Nothing in here may take Center down: every failure is logged once and turns into silence.
    /// </summary>
    internal static class UiSounds
    {
        private const int SampleRate = 48000;
        private const int LatencyMs = 40;

        private static float _effectsVolume = Core.CenterSettings.EffectsVolume / 100f;
        private static float _musicVolume = Core.CenterSettings.MusicVolume / 100f;

        /// <summary>0..1. Applies to the NEXT effect - one already playing keeps its loudness, it is
        /// over within a fraction of a second anyway.</summary>
        public static float EffectsVolume
        {
            get => Volatile.Read(ref _effectsVolume);
            set => Volatile.Write(ref _effectsVolume, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>0..1. Read by the music on every buffer, so a change is heard at once.</summary>
        public static float MusicVolume
        {
            get => Volatile.Read(ref _musicVolume);
            set => Volatile.Write(ref _musicVolume, Math.Clamp(value, 0f, 1f));
        }

        /// <summary>The same effect twice within this window plays once. A stick that crosses the
        /// deadzone on two axes in one tick raises two directions, and two identical clicks on top of
        /// each other only read as one louder, rougher click.</summary>
        private static readonly TimeSpan RepeatGuard = TimeSpan.FromMilliseconds(35);

        /// <summary>Idle ticks (one per second) with nothing audible before the stream pauses.</summary>
        private const int IdleTicksBeforePause = 3;

        private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        private static readonly string OverrideFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawTweaks", "Center", "sounds");
        private static readonly string[] Extensions = { ".ogg", ".wav", ".mp3" };

        private static readonly object Gate = new object();
        private static readonly Dictionary<UiSound, float[]> Clips = new Dictionary<UiSound, float[]>();
        private static readonly Dictionary<UiSound, DateTime> LastPlayed = new Dictionary<UiSound, DateTime>();
        private static readonly HashSet<string> LoggedOnce = new HashSet<string>(StringComparer.Ordinal);

        private static List<SoundFile> _musicTracks = new List<SoundFile>();
        private static MixingSampleProvider _mixer;
        private static WasapiOut _output;
        private static MusicProvider _music;
        private static Timer _idleTimer;
        private static int _idleTicks;
        private static Task _warmTask;
        private static bool _musicWanted;
        private static int _playCount;

        /// <summary>Mirrors CenterSettings.InterfaceSounds, so a press does not read the registry.</summary>
        public static bool EffectsEnabled { get; set; } = Core.CenterSettings.InterfaceSounds;

        /// <summary>
        /// How many times an effect was ASKED for, whether or not it was audible.
        ///
        /// Lets the caller of an action tell whether the action chose its own sound: A on the launch
        /// prompt plays Launch, and the generic Confirm must then not play on top of it. Counted even
        /// without a file, so the answer does not change when the sound files do.
        /// </summary>
        public static int PlayCount => Volatile.Read(ref _playCount);

        /// <summary>Loads the files and opens the device, off the UI thread. Safe to call often.</summary>
        public static Task WarmAsync()
        {
            lock (Gate)
            {
                return _warmTask ??= Task.Run(() =>
                {
                    try { LoadFiles(); }
                    catch (Exception ex) { LogOnce("load", "[Sound] reading the sound files failed: " + ex.Message); }
                    // After loading, so music asked for before the files were read starts now.
                    lock (Gate) ApplyMusicLocked();
                });
            }
        }

        public static void Play(UiSound sound)
        {
            if (!EffectsEnabled) return;
            Interlocked.Increment(ref _playCount);

            try
            {
                lock (Gate)
                {
                    var now = DateTime.UtcNow;
                    if (LastPlayed.TryGetValue(sound, out var last) && now - last < RepeatGuard) return;
                    LastPlayed[sound] = now;

                    // Not loaded yet (the warm-up is still running) or no file: silence. Waiting for
                    // the load here would stall the press that asked for it.
                    if (!Clips.TryGetValue(sound, out var clip) || clip == null) return;
                    if (!EnsureOutputLocked()) return;

                    _mixer.AddMixerInput(new ClipProvider(clip, EffectsVolume));
                    ResumeLocked();
                }
            }
            catch (Exception ex) { LogOnce("play", "[Sound] playing an effect failed: " + ex.Message); }
        }

        /// <summary>Fades the music in or out. The track position is kept while it is faded out, so
        /// coming back to the library carries on where it stopped.</summary>
        public static void SetMusicPlaying(bool playing)
        {
            try
            {
                lock (Gate)
                {
                    if (_musicWanted == playing) return;
                    _musicWanted = playing;
                    ApplyMusicLocked();
                }
            }
            catch (Exception ex) { LogOnce("music", "[Sound] switching the music failed: " + ex.Message); }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                _idleTimer?.Dispose();
                _idleTimer = null;
                DisposeOutputLocked();
                _music?.Dispose();
                _music = null;
            }
        }

        private static void ApplyMusicLocked()
        {
            if (_musicWanted)
            {
                if (_musicTracks.Count == 0) return;   // no track, or the warm-up has not read them yet
                if (!EnsureOutputLocked()) return;
                if (_music == null)
                {
                    _music = new MusicProvider(_musicTracks);
                    _mixer.AddMixerInput(_music);
                }
                _music.FadeIn();
                ResumeLocked();
            }
            else
            {
                // Faded, not cut: the idle timer pauses the stream once the fade has reached silence.
                _music?.FadeOut();
            }
        }

        #region Output
        private static bool EnsureOutputLocked()
        {
            if (_output != null) return true;
            try
            {
                _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
                _output = new WasapiOut(AudioClientShareMode.Shared, true, LatencyMs);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(_mixer);

                // The music provider belonged to the old mixer. It keeps its track and position and
                // joins the new one.
                if (_music != null) _mixer.AddMixerInput(_music);
                return true;
            }
            catch (Exception ex)
            {
                LogOnce("device", "[Sound] no audio output: " + ex.Message);
                DisposeOutputLocked();
                return false;
            }
        }

        /// <summary>A stopped stream WITH an exception is a lost device - headphones unplugged, the
        /// endpoint changed. It is rebuilt on the next sound rather than retried here, which would
        /// loop against a device that is gone.</summary>
        private static void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            LogOnce("stopped", "[Sound] audio output stopped: " + e.Exception.Message);
            // Not inside this callback: it runs on the stream's own thread, and disposing the stream
            // from there waits for that thread.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (Gate) { if (ReferenceEquals(sender, _output)) DisposeOutputLocked(); }
            });
        }

        private static void DisposeOutputLocked()
        {
            var output = _output;
            _output = null;
            _mixer = null;
            if (output == null) return;
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Dispose(); } catch { }
        }

        private static void ResumeLocked()
        {
            _idleTicks = 0;
            if (_output == null) return;
            if (_output.PlaybackState != PlaybackState.Playing) _output.Play();
            _idleTimer ??= new Timer(OnIdleTick, null, 1000, 1000);
        }

        private static void OnIdleTick(object state)
        {
            try
            {
                lock (Gate)
                {
                    if (_output == null || _mixer == null) { StopIdleTimerLocked(); return; }

                    bool effectsSounding = _mixer.MixerInputs.Any(i => !ReferenceEquals(i, _music));
                    bool musicSounding = _music != null && !_music.IsSilent;
                    if (effectsSounding || musicSounding) { _idleTicks = 0; return; }

                    if (++_idleTicks < IdleTicksBeforePause) return;
                    if (_output.PlaybackState == PlaybackState.Playing) _output.Pause();
                    StopIdleTimerLocked();
                }
            }
            catch (Exception ex) { LogOnce("idle", "[Sound] pausing the output failed: " + ex.Message); }
        }

        private static void StopIdleTimerLocked()
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
            _idleTicks = 0;
        }
        #endregion

        #region Files
        private sealed class SoundFile
        {
            public string Name;       // without extension, lower case
            public string Extension;  // ".ogg", lower case
            public byte[] Data;
            public string Origin;     // for the log
        }

        private static void LoadFiles()
        {
            var files = new Dictionary<string, SoundFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in ReadEmbedded()) files[f.Name] = f;
            foreach (var f in ReadOverrideFolder()) files[f.Name] = f;   // loose files win

            var clips = new Dictionary<UiSound, float[]>();
            foreach (UiSound sound in Enum.GetValues(typeof(UiSound)))
            {
                string name = sound.ToString().ToLowerInvariant();
                if (!files.TryGetValue(name, out var file)) continue;
                try { clips[sound] = Decode(file); }
                catch (Exception ex) { LogOnce("decode:" + name, "[Sound] " + file.Origin + " could not be read: " + ex.Message); }
            }

            var music = files.Values
                .Where(f => f.Name.StartsWith("music", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (Gate)
            {
                foreach (var kv in clips) Clips[kv.Key] = kv.Value;
                _musicTracks = music;
            }

            Core.InstallLog.Write("[Sound] effects: "
                + (clips.Count == 0 ? "none" : string.Join(", ", clips.Keys.Select(k => k.ToString().ToLowerInvariant())))
                + "; music tracks: " + music.Count);
        }

        private static IEnumerable<SoundFile> ReadEmbedded()
        {
            var result = new List<SoundFile>();
            var asm = Assembly.GetExecutingAssembly();
            // WPF puts every <Resource> into "<AssemblyName>.g.resources", keyed by the lower-cased
            // path. The assembly name carries the version (CTW_Center_x.y.z_Setup), so it is read,
            // never written out.
            using var stream = asm.GetManifestResourceStream(asm.GetName().Name + ".g.resources");
            if (stream == null) return result;

            using var reader = new ResourceReader(stream);
            foreach (DictionaryEntry entry in reader)
            {
                if (!(entry.Key is string key) || !key.StartsWith("assets/sounds/", StringComparison.Ordinal)) continue;
                if (!(entry.Value is Stream data)) continue;

                string ext = Path.GetExtension(key);
                if (!Extensions.Contains(ext)) continue;

                using var copy = new MemoryStream();
                data.CopyTo(copy);
                result.Add(new SoundFile
                {
                    Name = Path.GetFileNameWithoutExtension(key),
                    Extension = ext,
                    Data = copy.ToArray(),
                    Origin = "embedded " + key,
                });
            }
            return result;
        }

        private static IEnumerable<SoundFile> ReadOverrideFolder()
        {
            var result = new List<SoundFile>();
            if (!Directory.Exists(OverrideFolder)) return result;

            foreach (string path in Directory.GetFiles(OverrideFolder))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (!Extensions.Contains(ext)) continue;
                try
                {
                    result.Add(new SoundFile
                    {
                        Name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant(),
                        Extension = ext,
                        Data = File.ReadAllBytes(path),
                        Origin = path,
                    });
                }
                catch (Exception ex) { LogOnce("read:" + path, "[Sound] " + path + " could not be read: " + ex.Message); }
            }
            return result;
        }

        /// <summary>A reader for one file plus the samples it produces in the MIXER's format.</summary>
        private static ISampleProvider Open(SoundFile file, out IDisposable owner)
        {
            var bytes = new MemoryStream(file.Data, writable: false);
            WaveStream reader;
            ISampleProvider samples;
            switch (file.Extension)
            {
                case ".ogg":
                    var vorbis = new VorbisWaveReader(bytes, true);
                    reader = vorbis;
                    samples = vorbis;
                    break;
                case ".wav":
                    reader = new WaveFileReader(bytes);
                    samples = reader.ToSampleProvider();
                    break;
                default:
                    // Media Foundation decodes MP3 on every Windows 10/11. It is only used for music,
                    // which is opened on the audio thread (MTA) - where Media Foundation wants to be.
                    reader = new StreamMediaFoundationReader(bytes);
                    samples = reader.ToSampleProvider();
                    break;
            }
            owner = reader;

            if (samples.WaveFormat.Channels > 2)
            {
                reader.Dispose();
                throw new NotSupportedException(samples.WaveFormat.Channels + " channels");
            }
            if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);
            if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
            return samples;
        }

        private static float[] Decode(SoundFile file)
        {
            var samples = Open(file, out var owner);
            try
            {
                var all = new List<float>(SampleRate);
                var buffer = new float[8192];
                int read;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                    for (int i = 0; i < read; i++) all.Add(buffer[i]);
                return all.ToArray();
            }
            finally { owner.Dispose(); }
        }
        #endregion

        private static void LogOnce(string key, string message)
        {
            lock (LoggedOnce) { if (!LoggedOnce.Add(key)) return; }
            try { Core.InstallLog.Write(message); } catch { }
        }

        /// <summary>One decoded effect. Returning fewer samples than asked is how the mixer learns it
        /// has finished - it drops the input on its own.</summary>
        private sealed class ClipProvider : ISampleProvider
        {
            private readonly float[] _clip;
            private readonly float _volume;
            private int _position;

            public ClipProvider(float[] clip, float volume) { _clip = clip; _volume = volume; }

            public WaveFormat WaveFormat => MixFormat;

            public int Read(float[] buffer, int offset, int count)
            {
                int n = Math.Min(count, _clip.Length - _position);
                for (int i = 0; i < n; i++) buffer[offset + i] = _clip[_position + i] * _volume;
                _position += n;
                return n;
            }
        }

        /// <summary>
        /// The music: the tracks one after another, looped, with a fade in both directions.
        ///
        /// It always returns a full buffer, so it stays in the mixer for good. Faded out, it stops
        /// READING the track - the position is where the fade ended, and fading in carries on from
        /// there instead of from the start.
        /// </summary>
        private sealed class MusicProvider : ISampleProvider, IDisposable
        {
            /// <summary>Seconds for a full fade. Leaving the library cuts nothing off mid-note.</summary>
            private const float FadeSeconds = 0.8f;

            private readonly List<SoundFile> _tracks;
            private readonly float _step;
            private readonly object _chainGate = new object();
            private ISampleProvider _chain;
            private IDisposable _owner;
            private int _index = -1;
            private float _gain;
            private volatile float _target;

            public MusicProvider(List<SoundFile> tracks)
            {
                _tracks = tracks;
                _step = 1f / (SampleRate * 2 * FadeSeconds);
            }

            public WaveFormat WaveFormat => MixFormat;

            public bool IsSilent => _target <= 0 && _gain <= 0;

            public void FadeIn() => _target = 1f;
            public void FadeOut() => _target = 0f;

            public int Read(float[] buffer, int offset, int count)
            {
                if (IsSilent)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                int filled;
                lock (_chainGate) filled = FillFromTracks(buffer, offset, count);
                if (filled < count) Array.Clear(buffer, offset + filled, count - filled);

                float target = _target;
                float volume = MusicVolume;   // once per buffer: a slider change lands within 40 ms
                for (int i = 0; i < count; i++)
                {
                    if (_gain < target) _gain = Math.Min(target, _gain + _step);
                    else if (_gain > target) _gain = Math.Max(target, _gain - _step);
                    buffer[offset + i] *= _gain * volume;
                }
                return count;
            }

            private int FillFromTracks(float[] buffer, int offset, int count)
            {
                int filled = 0;
                int emptyOpens = 0;
                while (filled < count)
                {
                    if (_chain == null && !OpenNext()) return filled;

                    int n = _chain.Read(buffer, offset + filled, count - filled);
                    if (n > 0) { filled += n; emptyOpens = 0; continue; }

                    // End of the track: next one, wrapping. A whole round of tracks that give nothing
                    // (all broken) ends the attempt for this buffer rather than spinning the audio thread.
                    CloseChain();
                    if (++emptyOpens > _tracks.Count) return filled;
                }
                return filled;
            }

            private bool OpenNext()
            {
                for (int attempt = 0; attempt < _tracks.Count; attempt++)
                {
                    _index = (_index + 1) % _tracks.Count;
                    var track = _tracks[_index];
                    try
                    {
                        _chain = Open(track, out _owner);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LogOnce("music:" + track.Name, "[Sound] " + track.Origin + " could not be played: " + ex.Message);
                    }
                }
                return false;
            }

            private void CloseChain()
            {
                try { _owner?.Dispose(); } catch { }
                _owner = null;
                _chain = null;
            }

            public void Dispose()
            {
                lock (_chainGate) CloseChain();
            }
        }
    }
}

using System;

namespace ClawTweaksCenter.Core.Sources
{
    /// <summary>An installable build offered in the Center menu, from any of the three sources.</summary>
    public sealed class BuildSource
    {
        public string Origin;        // "Release" | "Test build" | "Nightly"
        public string Version;       // version without tag prefix, for display + sort
        public string Title;         // release name, or file name for nightlies
        public DateTime When;        // published_at / modifiedTime
        public long? SizeBytes;
        public string MsixUrl;       // the widget package; always present - a build without one is not listed
        public string Body;          // GitHub release body (markdown) for the "What's new" panel; null for nightlies

        public string SizeLabel => SizeBytes.HasValue ? $"{SizeBytes.Value / 1024.0 / 1024.0:0.#} MB" : null;
    }
}

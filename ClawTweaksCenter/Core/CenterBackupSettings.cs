using System.Collections.Generic;
using Microsoft.Win32;

namespace ClawTweaksCenter.Core
{
    // Keep registry access at the storage boundary: the restore transaction also owns file I/O,
    // and must be able to snapshot and roll back every registry kind without a lossy JSON export.
    internal sealed record CenterBackupSetting(string Name, RegistryValueKind Kind, object Value);

    internal interface ICenterBackupSettings
    {
        IReadOnlyList<CenterBackupSetting> ReadAll();
        void Delete(string name);
        void Set(CenterBackupSetting value);
    }

    internal sealed class RegistryCenterBackupSettings(string keyPath) : ICenterBackupSettings
    {
        public IReadOnlyList<CenterBackupSetting> ReadAll()
        {
            var values = new List<CenterBackupSetting>();
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (key != null)
                    foreach (var name in key.GetValueNames())
                        values.Add(new CenterBackupSetting(name, key.GetValueKind(name),
                            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
            }
            return values;
        }

        public void Delete(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
                key?.DeleteValue(name, throwOnMissingValue: false);
        }

        public void Set(CenterBackupSetting value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
                key.SetValue(value.Name, value.Value, value.Kind);
        }
    }
}

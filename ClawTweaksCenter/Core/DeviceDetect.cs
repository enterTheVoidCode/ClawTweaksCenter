using System;
using System.Management;
using Shared.Data;

namespace ClawTweaksCenter.Core
{
    /// <summary>
    /// Lightweight, Setup-scoped device detection for the Center menu banner ("which handheld did we
    /// find, do we support it"). The WMI reads mirror the Helper's
    /// XboxGamingBarHelper/Devices/DeviceDetector.cs::QueryDeviceInfoCombined; the model matching is
    /// not duplicated at all but shared — both call Shared/Data/ClawHardwareId.cs, so the two
    /// processes cannot disagree about a device. No disk cache / debug.json here — this runs once per
    /// Setup launch, and only the two MSI Claw generations the team is actively developing on are
    /// recognized; other device families (Legion, ASUS, ...) are out of scope for this installer.
    /// </summary>
    public static class DeviceDetect
    {
        /// <summary>Which device photo to show — see Ui/DeviceIcons.cs.</summary>
        public enum Model { Unknown, A2VM, Ex }

        public readonly struct Result
        {
            public readonly Model Model;
            public readonly string DisplayName;
            public readonly bool Supported;
            public Result(Model model, string displayName, bool supported)
            {
                Model = model; DisplayName = displayName; Supported = supported;
            }
        }

        /// <summary>
        /// Debug-only override so the Center's device-specific UI (icon, gating) can be exercised
        /// without the actual hardware — set from a --device=8ai/8ex CLI arg in App.xaml.cs.
        /// </summary>
        public static Model? DebugOverrideModel;

        public static Result Detect()
        {
            if (DebugOverrideModel.HasValue)
            {
                var m = DebugOverrideModel.Value;
                return m switch
                {
                    Model.A2VM => new Result(Model.A2VM, "MSI Claw (A2VM) — DEBUG", true),
                    Model.Ex => new Result(Model.Ex, "MSI Claw 8 EX AI+ CG3EM — DEBUG", true),
                    _ => new Result(Model.Unknown, "Unknown device — DEBUG", false),
                };
            }

            // One ladder for both processes: product name, then board/SKU code, then CPU corroborated
            // by the Claw controller. Center must reach the same verdict as the helper, or a device
            // whose product name is a factory placeholder would be offered no install here while the
            // helper happily drives it. See Shared/Data/ClawHardwareId.cs.
            var identity = ClawHardwareId.Resolve(QueryIdentity(),
                                                  ClawHardwareId.IsClawControllerInDeviceTree());

            return identity.Model switch
            {
                ClawHardwareModel.A2VM => new Result(Model.A2VM, "MSI Claw (A2VM)", true),
                ClawHardwareModel.Ex => new Result(Model.Ex, "MSI Claw 8 EX AI+ CG3EM", true),
                _ => new Result(Model.Unknown, "Unknown device", false),
            };
        }

        /// <summary>
        /// Oldest ClawTweaks version this device is actually supported on, or null if there's no
        /// floor. The Claw 8 EX (Panther Lake) only landed proper support in 0.1.7.63 — anything
        /// older predates the port and shouldn't be offered for install on that device.
        /// </summary>
        public static Version MinimumSupportedVersion(Model model) => model switch
        {
            Model.Ex => new Version(0, 1, 7, 63),
            _ => null,
        };

        /// <summary>
        /// Reads every identity string the ladder can use, on one WMI connection. Mirrors the helper's
        /// DeviceDetector.QueryDeviceInfoCombined; anything read here must be read there too, or the
        /// two processes can disagree about the same machine.
        /// </summary>
        private static ClawIdentitySources QueryIdentity()
        {
            var sources = new ClawIdentitySources();
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();

                using (var csp = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Vendor, Name FROM Win32_ComputerSystemProduct")))
                {
                    csp.Options.Timeout = TimeSpan.FromSeconds(3);
                    foreach (var obj in csp.Get())
                    {
                        sources.Manufacturer = obj["Vendor"]?.ToString()?.Trim();
                        sources.ProductName = obj["Name"]?.ToString()?.Trim();
                        break;
                    }
                }

                using (var cs = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT SystemSKUNumber FROM Win32_ComputerSystem")))
                {
                    cs.Options.Timeout = TimeSpan.FromSeconds(3);
                    foreach (var obj in cs.Get())
                    {
                        sources.SystemSku = obj["SystemSKUNumber"]?.ToString()?.Trim();
                        break;
                    }
                }

                using (var bb = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Product FROM Win32_BaseBoard")))
                {
                    bb.Options.Timeout = TimeSpan.FromSeconds(3);
                    foreach (var obj in bb.Get())
                    {
                        sources.BaseBoardProduct = obj["Product"]?.ToString()?.Trim();
                        break;
                    }
                }

                using (var cpu = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT Name, Caption FROM Win32_Processor")))
                {
                    cpu.Options.Timeout = TimeSpan.FromSeconds(3);
                    foreach (var obj in cpu.Get())
                    {
                        sources.ProcessorName = obj["Name"]?.ToString()?.Trim();
                        sources.ProcessorCaption = obj["Caption"]?.ToString()?.Trim();
                        break;
                    }
                }
            }
            catch { /* leave whatever was read — Detect() falls through to "Unknown device" */ }
            return sources;
        }
    }
}

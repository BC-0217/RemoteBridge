using System;
using System.Reflection;

namespace PeerDesk.Windows {
    internal static class BuildVersion {
        internal static string Display {
            get {
                var value = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    typeof(BuildVersion).Assembly, typeof(AssemblyInformationalVersionAttribute));
                return value == null ? typeof(BuildVersion).Assembly.GetName().Version.ToString() : value.InformationalVersion;
            }
        }
    }
}

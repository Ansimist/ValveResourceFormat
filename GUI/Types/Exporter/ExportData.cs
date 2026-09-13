using GUI.Utils;
using ValvePak;

namespace GUI.Types.Exporter
{
    class ExportData
    {
        public PackageEntry? PackageEntry { get; set; }
        public required VrfGuiContext VrfGuiContext { get; set; }
        public IDisposable? DisposableContents { get; set; }
    }
}

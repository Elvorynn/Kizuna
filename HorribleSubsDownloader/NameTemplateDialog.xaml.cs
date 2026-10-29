using MahApps.Metro.Controls;
using System.Windows;
using System.Windows.Controls;

namespace Kizuna
{
    public partial class NameTemplateDialog : MetroWindow
    {
        private readonly int    _episodeNumber;
        private readonly string _episodeType;

        public string ResultTemplate { get; private set; }

        public NameTemplateDialog(string currentTemplate, int episodeNumber, string episodeType)
        {
            InitializeComponent();
            _episodeNumber = episodeNumber;
            _episodeType   = episodeType;
            TxtTemplate.Text = currentTemplate ?? "";
            UpdatePreview();
        }

        private void TxtTemplate_TextChanged(object sender, TextChangedEventArgs e)
            => UpdatePreview();

        private void UpdatePreview()
        {
            string s = TxtTemplate.Text;
            s = s.Replace("%e", _episodeNumber >= 0 ? _episodeNumber.ToString("D2") : "??");
            s = s.Replace("%t", _episodeType ?? "");
            TxtPreview.Text = s.Trim().TrimEnd('-').Trim();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ResultTemplate = TxtTemplate.Text;
            DialogResult   = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}

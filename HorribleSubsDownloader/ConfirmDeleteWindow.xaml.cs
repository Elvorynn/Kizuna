using System.Windows;
using MahApps.Metro.Controls;

namespace Kizuna
{
    /// <summary>
    /// Modal that asks the user to confirm deleting a backend entry.
    /// Cancel is the default button; <see cref="Window.DialogResult"/> is true only on confirm.
    /// </summary>
    public partial class ConfirmDeleteWindow : MetroWindow
    {
        public ConfirmDeleteWindow(string filename)
        {
            InitializeComponent();

            Title              = LocalizationManager.Get("ConfirmDeleteTitle");
            TxtHeader.Text     = LocalizationManager.Get("ConfirmDeleteEntry");
            TxtFilename.Text   = filename;
            BtnCancel.Content  = LocalizationManager.Get("BtnCancel");
            BtnConfirm.Content = LocalizationManager.Get("BtnDelete");
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}

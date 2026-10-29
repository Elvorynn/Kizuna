using MahApps.Metro.Controls;
using System.Windows;

namespace Kizuna
{
    public partial class LibraryRuleEditWindow : MetroWindow
    {
        public LibraryRule Result { get; private set; }

        public LibraryRuleEditWindow(LibraryRule rule = null)
        {
            InitializeComponent();

            // Work on a copy so cancelling doesn't mutate the original
            Result = rule != null
                ? new LibraryRule
                {
                    Id                  = rule.Id,
                    Name                = rule.Name,
                    SourcePath          = rule.SourcePath,
                    MatchSubfolders     = rule.MatchSubfolders,
                    TargetBasePath      = rule.TargetBasePath,
                    CreateFranchiseFolder        = rule.CreateFranchiseFolder,
                    CreateSeasonFolderIfMultiple = rule.CreateSeasonFolderIfMultiple,
                    UseCleanFilename             = rule.UseCleanFilename
                }
                : new LibraryRule();

            PopulateFields();
            ApplyLanguage();
        }

        private void PopulateFields()
        {
            TxtName.Text                 = Result.Name;
            TxtSourcePath.Text           = Result.SourcePath;
            ChkMatchSubfolders.IsChecked = Result.MatchSubfolders;
            TxtTargetPath.Text           = Result.TargetBasePath;
            ChkFranchiseFolder.IsChecked = Result.CreateFranchiseFolder;
            ChkSeasonFolder.IsChecked    = Result.CreateSeasonFolderIfMultiple;
            ChkSeasonFolder.IsEnabled    = Result.CreateFranchiseFolder;
            ChkCleanFilename.IsChecked   = Result.UseCleanFilename;
        }

        private void ApplyLanguage()
        {
            Title = LocalizationManager.Get("RuleEditTitle");

            TxtLblName.Text    = LocalizationManager.Get("RuleEditName");
            TxtLblSource.Text  = LocalizationManager.Get("RuleEditSourcePath");
            TxtLblTarget.Text  = LocalizationManager.Get("RuleEditTargetPath");

            ChkMatchSubfolders.Content   = LocalizationManager.Get("RuleEditMatchSubs");
            ChkFranchiseFolder.Content   = LocalizationManager.Get("RuleEditFranchise");
            ChkSeasonFolder.Content      = LocalizationManager.Get("RuleEditSeasonIfMultiple");
            ChkCleanFilename.Content     = LocalizationManager.Get("RuleEditCleanFile");

            TxtSourceError.Text = "⚠  " + LocalizationManager.Get("RuleEditSourcePath") + " is required";
            TxtTargetError.Text = "⚠  " + LocalizationManager.Get("RuleEditTargetPath") + " is required";

            BtnOk.Content     = LocalizationManager.Get("BtnSave");
            BtnCancel.Content = LocalizationManager.Get("BtnCancel");
        }

        private void ChkFranchiseFolder_Changed(object sender, RoutedEventArgs e)
        {
            ChkSeasonFolder.IsEnabled = ChkFranchiseFolder.IsChecked == true;
            if (ChkSeasonFolder.IsEnabled == false)
                ChkSeasonFolder.IsChecked = false;
        }

        private void TxtSourcePath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => TxtSourceError.Visibility = Visibility.Collapsed;

        private void TxtTargetPath_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => TxtTargetError.Visibility = Visibility.Collapsed;

        private void BtnBrowseSource_Click(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(initialPath: TxtSourcePath.Text, title: "Select source folder");
            if (picked != null)
            {
                TxtSourcePath.Text        = picked;
                TxtSourceError.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnBrowseTarget_Click(object sender, RoutedEventArgs e)
        {
            string picked = FolderPicker.Pick(initialPath: TxtTargetPath.Text, title: "Select target folder");
            if (picked != null)
            {
                TxtTargetPath.Text        = picked;
                TxtTargetError.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            bool valid = true;

            if (string.IsNullOrWhiteSpace(TxtSourcePath.Text))
            {
                TxtSourceError.Visibility = Visibility.Visible;
                valid = false;
            }
            if (string.IsNullOrWhiteSpace(TxtTargetPath.Text))
            {
                TxtTargetError.Visibility = Visibility.Visible;
                valid = false;
            }

            if (!valid) return;

            Result.Name                  = string.IsNullOrWhiteSpace(TxtName.Text) ? "Rule" : TxtName.Text.Trim();
            Result.SourcePath            = TxtSourcePath.Text.Trim();
            Result.MatchSubfolders              = ChkMatchSubfolders.IsChecked == true;
            Result.TargetBasePath               = TxtTargetPath.Text.Trim();
            Result.CreateFranchiseFolder        = ChkFranchiseFolder.IsChecked == true;
            Result.CreateSeasonFolderIfMultiple = ChkSeasonFolder.IsChecked == true;
            Result.UseCleanFilename             = ChkCleanFilename.IsChecked == true;

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}

#nullable enable

using Playnite.SDK.Models;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using static SteamVersionChecker.SteamVersionChecker;

namespace SteamVersionChecker
{
    /// <summary>
    /// Interaction logic for VersionInput.xaml
    /// </summary>
    public partial class VersionInput : UserControl
    {
        private readonly Game game;
        private readonly GameData gameData;
        private readonly SteamVersionChecker steamVersionChecker;

        public VersionInput(GameData gameData, Game game, SteamVersionChecker steamVersionChecker)
        {
            InitializeComponent();

            this.game = game;
            this.gameData = gameData;
            this.steamVersionChecker = steamVersionChecker;

            txtLatestVersion.Text = game.Version ?? "";
            txtPlayedVersion.Text = gameData.PlayedVersion ?? "";
            txtUpdateMonths.Text = gameData.UpdateMonths.ToString(CultureInfo.InvariantCulture);
            txtLastUpdated.Text = DateTimeOffset.FromUnixTimeSeconds(gameData.LastUpdatedSeconds).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        }

        private async void BtnFetch_Click(object sender, RoutedEventArgs e)
        {
            await steamVersionChecker.SetVersionAsync(game, setTag: false);
            txtLatestVersion.Text = game.Version ?? "";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var playedVersionChanged = gameData.PlayedVersion != txtPlayedVersion.Text;
            var updateMonthsChanged = gameData.UpdateMonths.ToString(CultureInfo.InvariantCulture) != txtUpdateMonths.Text;
            var versionChanged = game.Version != txtLatestVersion.Text;

            var selectedTimeSeconds = DateTimeOffset.ParseExact(
                txtLastUpdated.Text,
                "yyyy/MM/dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
            ).ToUnixTimeSeconds();

            var lastUpdatedChanged = gameData.LastUpdatedSeconds != selectedTimeSeconds;

            if (playedVersionChanged || updateMonthsChanged || lastUpdatedChanged)
            {
                float.TryParse(
                    s: txtUpdateMonths.Text,
                    style: NumberStyles.Float | NumberStyles.AllowThousands,
                    provider: CultureInfo.InvariantCulture.NumberFormat,
                    result: out var updateMonths
                );

                gameData.UpdateMonths = updateMonths;
                gameData.PlayedVersion = txtPlayedVersion.Text;
                gameData.LastUpdatedSeconds = selectedTimeSeconds;

                steamVersionChecker.SaveData();
            }

            if (versionChanged)
            {
                game.Version = txtLatestVersion.Text;
                steamVersionChecker.PlayniteApi.Database.Games.Update(game);
            }

            Window.GetWindow(this).Close();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            game.Version = "";
            steamVersionChecker.userData.Remove(game.Id.ToString());

            steamVersionChecker.SaveData();
            steamVersionChecker.PlayniteApi.Database.Games.Update(game);

            Window.GetWindow(this).Close();
        }
    }
}

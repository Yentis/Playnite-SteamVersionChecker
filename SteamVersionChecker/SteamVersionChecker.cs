#nullable enable

using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using SteamKit2;
using SteamVersionChecker.Properties;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SteamVersionChecker
{
    public class SteamVersionChecker : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly HttpClient httpClient = new HttpClient();
        private readonly SteamClient steamClient = new SteamClient();
        private SteamUser? steamUser;
        private SteamApps? steamApps;

        public Dictionary<string, GameData> userData = new Dictionary<string, GameData>();

        private bool started = false;
        private bool isRunning = true;
        private bool isLoggedIn = false;

        private SteamVersionCheckerSettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("fbc3b6e5-1010-427e-9d4b-2d9cf96ea8cc");

        public class GameData
        {
            public string PlayedVersion { get; set; }
            public float UpdateMonths { get; set; }
            public long LastUpdatedSeconds { get; set; }

            public GameData(string playedVersion)
            {
                PlayedVersion = playedVersion;
                UpdateMonths = 0;
                LastUpdatedSeconds = 0;
            }
        }

        public class ReviewsResponse
        {
            public List<Reviews>? reviews;
            public string? cursor;
        }

        public class Reviews
        {
            public ReviewsAuthor? author;
        }

        public class ReviewsAuthor
        {
            public long playtime_forever;
        }

        public class PlaytimeResult
        {
            public long average;
            public long median;

            public PlaytimeResult(long average, long median)
            {
                this.average = average;
                this.median = median;
            }
        }

        public SteamVersionChecker(IPlayniteAPI api) : base(api)
        {
            settings = new SteamVersionCheckerSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = false
            };

            var manager = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>();
            steamApps = steamClient.GetHandler<SteamApps>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            steamClient.Connect();

            Task.Run(() => {
                while (isRunning)
                {
                    manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                }
            });
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            steamUser?.LogOnAnonymous();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            isRunning = false;
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                logger.Error($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");
                isRunning = false;

                return;
            }

            isLoggedIn = true;
        }
        
        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            logger.Info($"Logged off of Steam: {callback.Result}");
            isLoggedIn = false;
        }

        private async Task<Game?> GetRandomGame(List<Game> games, DynamicMessage message)
        {
            games = new List<Game>(games);
            if (games.Count <= 0) return null;

            var random = new Random();
            var index = random.Next(games.Count);

            var game = games[index];
            if (game == null) return null;
            message.SetMessage($"Checking game...\n{game}");

            // TODO: remove when exclusion filtering is possible
            if (game.Tags != null && game.Tags.Any(tag => tag.Name == "No download" || tag.Name == "GPU Upgrade")) {
                games.Remove(game);
                return await GetRandomGame(games, message);
            }

            var lastUpdatedSeconds = await DoGetLastUpdatedSeconds(game) ?? 0;
            if (lastUpdatedSeconds <= 0) return game;

            var curTimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeSeconds = curTimeSeconds - lastUpdatedSeconds;
            var timeMonths = timeSeconds / 60 / 60 / 24 / 31;

            userData.TryGetValue(game.Id.ToString(), out var gameData);
            var updateMonths = gameData?.UpdateMonths ?? 0;
            var actualUpdateMonths = updateMonths == 0 ? 3 : updateMonths;

            if (timeMonths < actualUpdateMonths)
            {
                games.Remove(game);
                return await GetRandomGame(games, message);
            }

            return game;
        }

        private DynamicMessage CreateDynamicMessage()
        {
            var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false,
                ShowCloseButton = true,
            });

            window.ShowInTaskbar = false;
            window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Width = 300;
            window.Height = 240;

            var dynamicMessage = new DynamicMessage();
            window.Content = dynamicMessage;

            window.Show();
            return dynamicMessage;
        }

        private async Task<Game?> GetOldestGame(List<Game> games)
        {
            if (games.Count <= 0) return null;
            Game? curOldestGame = null;
            uint? curMinUpdatedSeconds = null;

            var gameDictionary = games.ToDictionary(game => game.Id.ToString());
            var lastUpdatedSecondsDictionary = await DoGetManyLastUpdatedSeconds(games);

            foreach (var item in lastUpdatedSecondsDictionary)
            {
                var gameId = item.Key;
                var updatedSeconds = item.Value;

                gameDictionary.TryGetValue(gameId, out var game);
                if (game == null) continue;

                if (curMinUpdatedSeconds == null || updatedSeconds < curMinUpdatedSeconds)
                {
                    curOldestGame = game;
                    curMinUpdatedSeconds = updatedSeconds;
                }
            }

            return curOldestGame;
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = $"@{strings.PluginName}",
                Description = strings.MenuGetRandomGame,
                Action = (data) => GetRandomGameAction(PlayniteApi.MainView.FilteredGames)
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{strings.PluginName}",
                Description = strings.MenuGetOldestGame,
                Action = (data) => GetOldestGameAction(PlayniteApi.MainView.FilteredGames)
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{strings.PluginName}",
                Description = strings.MenuSortLinks,
                Action = (data) =>
                {
                    var updatedGames = new List<Game>();

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        var links = game.Links ??= new ObservableCollection<Link>();
                        var newLinks = new ObservableCollection<Link>(links.OrderBy(link => link.Name));

                        var officialLink = newLinks.FirstOrDefault(link => link.Name.ToLower().StartsWith("official"));
                        var steamLink = newLinks.FirstOrDefault(link => link.Name.ToLower() == "steam");

                        if (officialLink != null)
                        {
                            officialLink.Name = "Official";
                            newLinks.Move(newLinks.IndexOf(officialLink), 0);
                        }

                        if (steamLink != null)
                        {
                            newLinks.Move(newLinks.IndexOf(steamLink), officialLink != null ? 1 : 0);
                        }

                        var hasChanged = !links.SequenceEqual(newLinks);
                        if (hasChanged)
                        {
                            game.Links = newLinks;
                            updatedGames.Add(game);
                        }
                    }

                    PlayniteApi.Database.Games.Update(updatedGames);
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        id: strings.MenuSortLinks,
                        text: $"Links sorted for {updatedGames.Count} games",
                        type: NotificationType.Info
                    ));
                },
            };

            yield return new MainMenuItem
            {
                MenuSection = $"@{strings.PluginName}",
                Description = strings.MenuAddMissingFieldTag,
                Action = (data) =>
                {
                    var tag = PlayniteApi.Database.Tags.FirstOrDefault(tag => tag.Name == strings.TagMissingField);
                    if (tag == null) return;

                    var updatedGames = new List<Game>();

                    foreach (var game in PlayniteApi.Database.Games)
                    {
                        var tags = game.TagIds ??= new List<Guid>();
                        var newTags = new List<Guid>(tags);

                        var hasPlatforms = game.Platforms != null && game.Platforms.Count > 0;
                        var hasMedia = !string.IsNullOrEmpty(game.Icon) && !string.IsNullOrEmpty(game.CoverImage) && !string.IsNullOrEmpty(game.BackgroundImage);
                        var hasLinks = game.Links != null && game.Links.Count > 0;
                        var hasDescription = !string.IsNullOrEmpty(game.Description);
                        var releaseDate = game.ReleaseDate.GetValueOrDefault();
                        var hasReleaseDate = releaseDate.Year > 0 && releaseDate.Month != null && releaseDate.Day != null;

                        if (hasPlatforms && hasMedia && hasLinks && hasDescription && hasReleaseDate)
                        {
                            newTags.Remove(tag.Id);
                        }
                        else
                        {
                            newTags.AddMissing(tag.Id);
                        }

                        var hasChanged = !tags.SequenceEqual(newTags);
                        if (hasChanged)
                        {
                            game.TagIds = newTags;
                            updatedGames.Add(game);
                        }
                    }


                    PlayniteApi.Database.Games.Update(updatedGames);
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        id: strings.MenuAddMissingFieldTag,
                        text: $"Tag updated for {updatedGames.Count} games",
                        type: NotificationType.Info
                    ));
                },
            };
        }

        private async void GetRandomGameAction(List<Game> games)
        {
            var message = CreateDynamicMessage();
            message.SetMessage(strings.GameSearching);

            var game = await GetRandomGame(games, message);
            if (game == null)
            {
                message.SetMessage(strings.GameNotFound);
            }
            else
            {
                PlayniteApi.MainView.SelectGame(game.Id);
                message.SetMessage($"{strings.GameFound}\n{game.Name}", closeAfter: 1000);
            }
        }

        private async void GetOldestGameAction(List<Game> games)
        {
            var message = CreateDynamicMessage();
            message.SetMessage(strings.GameSearching);

            var game = await GetOldestGame(games);
            if (game == null)
            {
                message.SetMessage(strings.GameNotFound);
            }
            else
            {
                PlayniteApi.MainView.SelectGame(game.Id);
                message.SetMessage($"{strings.GameFound}\n{game.Name}", closeAfter: 1000);
            }
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (!isLoggedIn || !isRunning) yield break;

            var game = args.Games.Count == 1 ? args.Games.FirstOrDefault() : null;
            GameData? gameData = null;
            if (game != null) userData.TryGetValue(game.Id.ToString(), out gameData);

            var gameText = game != null
                ? $" ({gameData?.PlayedVersion ?? "0"} / {(String.IsNullOrWhiteSpace(game.Version) ? "0" : game.Version)})"
                : "";

            if (game == null)
            {
                yield return new GameMenuItem
                {
                    MenuSection = strings.PluginName,
                    Description = strings.MenuGetRandomGame,
                    Action = (data) => GetRandomGameAction(args.Games)
                };

                yield return new GameMenuItem
                {
                    MenuSection = strings.PluginName,
                    Description = strings.MenuGetOldestGame,
                    Action = (data) => GetOldestGameAction(args.Games)
                };
            }

            yield return new GameMenuItem
            {
                MenuSection = strings.PluginName,
                Description = $"{strings.MenuSetVersions}{gameText}",
                Action = (data) =>
                {
                    if (game == null)
                    {
                        foreach (var game in data.Games)
                        {
                            _ = SetVersionAsync(game);
                        }

                        PlayniteApi.Dialogs.ShowMessage("Game version(s) updated");
                        SaveData();
                        return;
                    }

                    var window = PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
                    {
                        ShowMinimizeButton = false,
                        ShowMaximizeButton = false,
                        ShowCloseButton = true,
                    });

                    if (gameData == null)
                    {
                        gameData = new GameData(playedVersion: "");
                        userData[game.Id.ToString()] = gameData;
                    }

                    window.ShowInTaskbar = false;
                    window.Owner = PlayniteApi.Dialogs.GetCurrentAppWindow();
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    window.Width = 300;
                    window.Height = 240;
                    window.Content = new VersionInput(
                        gameData,
                        game,
                        steamVersionChecker: this
                    );

                    window.ShowDialog();
                }
            };

            if (game == null) yield break;

            yield return new GameMenuItem
            {
                MenuSection = strings.PluginName,
                Description = strings.MenuGetPlaytime,
                Action = (data) =>
                {
                    var steamId = GetSteamId(game);

                    DoGetPlaytime(steamId).ContinueWith((task) => {
                        var result = task.Result;

                        var averageHours = Decimal.Round(result.average / 60, 2);
                        var medianHours = Decimal.Round(result.median / 60, 2);

                        PlayniteApi.Dialogs.ShowMessage($"Average: {averageHours} hours | Median: {medianHours} hours");
                    });
                }
            };
        }

        private uint GetSteamId(Game game, bool notify = true)
        {
            var steamLink = game.Links?.FirstOrDefault(link => link.Name.ToLower() == "steam");
            if (steamLink == null)
            {
                if (notify) PlayniteApi.Notifications.Add(new NotificationMessage(
                    id: Id + game.Name,
                    text: $"No Steam URL found for {game.Name}, please set it in the Links tab",
                    type: NotificationType.Error
                ));
                return 0;
            }

            var linkSplit = steamLink.Url.Split(new[] { "/app/" }, StringSplitOptions.None);
            var steamIdString = linkSplit?.ElementAtOrDefault(1)?.Split('/').FirstOrDefault();
            if (steamIdString == null)
            {
                if (notify) PlayniteApi.Notifications.Add(new NotificationMessage(
                    id: Id + game.Name,
                    text: $"Could not find Steam ID for {game.Name} in URL: {steamLink.Url}",
                    type: NotificationType.Error
                ));
                return 0;
            }

            var success = UInt32.TryParse(steamIdString, out var steamId);
            if (!success)
            {
                if (notify) PlayniteApi.Notifications.Add(new NotificationMessage(
                    id: Id + game.Name,
                    text: $"Steam ID was not a number for {game.Name} in URL: {steamLink.Url}",
                    type: NotificationType.Error
                ));
                return 0;
            }

            return steamId;
        }

        public async Task SetVersionAsync(Game game, bool setTag = true)
        {
            var steamId = GetSteamId(game);
            if (steamId == 0) return;

            var result = await DoVersionRequestAsync(steamId);
            if (result == null)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    id: Id + game.Name,
                    text: $"Failed to update version for {game.Name}, see logs for details",
                    type: NotificationType.Error
                ));
                return;
            }

            var gameId = game.Id.ToString();
            userData.TryGetValue(gameId, out var gameData);

            var hasVersion = !String.IsNullOrWhiteSpace(game.Version) && game.Version != "0";
            var hasPlayedVersion = !String.IsNullOrWhiteSpace(gameData.PlayedVersion) && gameData.PlayedVersion != "0";

            if (hasVersion && hasPlayedVersion && gameData.PlayedVersion != result)
            {
                var tag = PlayniteApi.Database.Tags.FirstOrDefault(tag => tag.Name == strings.TagUpdateAvailable);
                if (tag != null && setTag)
                {
                    var tags = game.TagIds ??= new List<Guid>();
                    tags.AddMissing(tag.Id);
                    game.TagIds = tags;
                }
            }

            if (game.Version == result) return;
            game.Version = result;

            PlayniteApi.Database.Games.Update(game);
        }

        private async Task<string?> DoVersionRequestAsync(uint steamId)
        {
            if (steamApps == null)
            {
                logger.Error(strings.ErrorSteamAppsNotFound);
                return null;
            }

            var productJob = steamApps.PICSGetProductInfo(app: steamId, package: null);

            var resultSet = await productJob;
            var appInfo = resultSet.Results.FirstOrDefault()?.Apps[steamId];
            if (appInfo == null)
            {
                logger.Error($"Product info request failed: {resultSet.Results}");
                return null;
            }

            var depots = appInfo.KeyValues["depots"];
            if (depots == null)
            {
                logger.Error($"{strings.ErrorDepotsNotFound}: {appInfo}");
                return null;
            }

            var branches = depots.Children.Find(child => child.Name == "branches");
            if (branches == null) {
                logger.Error($"{strings.ErrorBranchesNotFound}: {depots}");
                return null;
            }

            var publicBranch = branches.Children.Find(child => child.Name == "public");
            if (publicBranch == null) {
                logger.Error($"No public branch found: {branches}");
                return null;
            }

            var buildId = publicBranch.Children.Find(child => child.Name == "buildid")?.Value;
            if (buildId == null) {
                logger.Error($"No build ID found: {publicBranch}");
                return null;
            }

            return buildId;
        }

        private async Task<uint?> DoGetLastUpdatedSeconds(Game game)
        {
            var games = new[] { game };
            var lastUpdatedSecondsDictionary = await DoGetManyLastUpdatedSeconds(games);

            lastUpdatedSecondsDictionary.TryGetValue(game.Id.ToString(), out var lastUpdatedSeconds);
            if (lastUpdatedSeconds <= 0) return null;

            return lastUpdatedSeconds;
        }

        private async Task<Dictionary<string, uint>> DoGetManyLastUpdatedSeconds(IEnumerable<Game> games)
        {
            var dictionary = new Dictionary<string, uint>();
            var requests = new List<SteamApps.PICSRequest>();
            var steamGameIds = new Dictionary<uint, string>();

            if (steamApps == null)
            {
                logger.Error(strings.ErrorSteamAppsNotFound);
                return dictionary;
            }

            foreach (var game in games)
            {
                var gameId = game.Id.ToString();
                userData.TryGetValue(gameId, out var gameData);
                var lastUpdatedSeconds = gameData?.LastUpdatedSeconds ?? 0;

                if (lastUpdatedSeconds > 0) {
                    dictionary[gameId] = (uint)lastUpdatedSeconds;
                    continue;
                }

                var steamId = GetSteamId(game, notify: false);
                if (steamId <= 0) continue;
                steamGameIds[steamId] = gameId;

                requests.Add(new SteamApps.PICSRequest(steamId));
            }

            var productJobs = steamApps.PICSGetProductInfo(apps: requests, packages: Enumerable.Empty<SteamApps.PICSRequest>());

            var resultSet = await productJobs;
            if (resultSet.Results == null) return dictionary;

            foreach (var productInfo in resultSet.Results)
            {
                foreach (var appInfo in productInfo.Apps)
                {
                    var depots = appInfo.Value.KeyValues["depots"];
                    if (depots == null)
                    {
                        logger.Error($"{strings.ErrorDepotsNotFound}: {appInfo.Value}");
                        continue;
                    }

                    var branches = depots.Children.Find(child => child.Name == "branches");
                    if (branches == null)
                    {
                        logger.Error($"{strings.ErrorBranchesNotFound}: {depots}");
                        continue;
                    }

                    var timeUpdatedString = branches.Children
                        .Where(branch => branch.Children.Any(child => child.Name == "timeupdated"))
                        .Select(branch => branch.Children.Find(child => child.Name == "timeupdated").Value)
                        .OrderByDescending(timeUpdated => timeUpdated)
                        .FirstOrDefault();

                    var success = UInt32.TryParse(timeUpdatedString, out var timeUpdated);
                    if (!success)
                    {
                        logger.Error($"No timeupdated found: {branches}");
                        continue;
                    }

                    steamGameIds.TryGetValue(appInfo.Key, out var gameId);
                    if (gameId == null)
                    {
                        logger.Error($"Steam game with no entry found: {appInfo.Key}");
                        continue;
                    }

                    dictionary[gameId] = timeUpdated;
                }
            }

            return dictionary;
        }

        private async Task<PlaytimeResult> DoGetPlaytime(uint steamId)
        {
            List<long> playtimes = new List<long>();
            bool hasReviews = true;
            long totalPlaytime = 0;
            int totalCount = 0;
            string? cursor = null;
            int page = 0;

            while (hasReviews)
            {
                var response = await DoGetReviewsPage(steamId, cursor);
                if (response == null) break;
                page++;

                var reviews = response.reviews ?? new List<Reviews>();
                hasReviews = reviews.Count() > 0;
                cursor = response.cursor;

                foreach (var review in reviews) {
                    var playtime = review.author?.playtime_forever ?? 0;
                    totalPlaytime += playtime;
                    playtimes.Add(playtime);
                }

                totalCount += reviews.Count();
            }

            if (totalCount <= 0)
            {
                return new PlaytimeResult(average: 0, median: 0);
            }

            var average = totalPlaytime / totalCount;
            var median = GetMedian(playtimes);

            return new PlaytimeResult(average: average, median: median);
        }

        private async Task<ReviewsResponse?> DoGetReviewsPage(uint steamId, string? cursor = null)
        {
            string url = $"https://store.steampowered.com/appreviews/{steamId}?json=1&filter=recent&num_per_page=100&language=all&purchase_type=all&filter_offtopic_activity=0";
            if (cursor != null)
            {
                url += $"&cursor={WebUtility.UrlEncode(cursor)}";
            }

            using HttpResponseMessage response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                logger.Error($"Failed to get reviews page {cursor} for {steamId}: {response.StatusCode} - {response.ReasonPhrase}");
                return null;
            }

            string jsonText = await response.Content.ReadAsStringAsync();
            bool success = Serialization.TryFromJson<ReviewsResponse>(jsonText, out ReviewsResponse? reviewsResponse);

            if (!success)
            {
                logger.Error($"Failed to serialize reviews response: {jsonText}");
                return null;
            }

            return reviewsResponse;
        }

        private long GetMedian(List<long> data)
        {
            data.Sort();

            int middle = data.Count / 2;
            long median;

            if (middle % 2 != 0)
            {
                median = data[middle];
            }
            else
            {
                median = (data[middle - 1] + data[middle]) / 2;
            }

            return median;
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (started) return;
            started = true;

            var tags = PlayniteApi.Database.Tags;
            if (!tags.Any(tag => tag.Name == strings.TagUpdateAvailable))
            {
                tags.Add(strings.TagUpdateAvailable);
            }

            if (!tags.Any(tag => tag.Name == strings.TagMissingField))
            {
                tags.Add(strings.TagMissingField);
            }

            var dataPath = GetPluginUserDataPath();
            var dataJsonPath = Path.Combine(dataPath, "data.json");

            Serialization.TryFromJsonFile(dataJsonPath, out userData);
            userData ??= new Dictionary<string, GameData>();

            CheckGames();

            PlayniteApi.Database.Games.ItemCollectionChanged += (sender, args) =>
            {
                if (args.RemovedItems.Any())
                {
                    foreach (var item in args.RemovedItems)
                    {
                        userData.Remove(item.Id.ToString());
                    }

                    SaveData();
                }
            };
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            started = false;
            isRunning = false;
            isLoggedIn = false;

            steamUser?.LogOff();
            steamUser = null;
            steamApps = null;

            steamClient.Disconnect();
            httpClient.Dispose();
        }

        private void CheckGames()
        {
            var removedGames = new List<string>();

            foreach (var gameId in userData.Keys)
            {
                var game = PlayniteApi.Database.Games.Get(new Guid(gameId));
                if (game == null)
                {
                    logger.Warn($"Game {gameId} removed");
                    removedGames.Add(gameId);
                    continue;
                }
            }

            foreach (var gameId in removedGames)
            {
                userData.Remove(gameId);
            }

            SaveData();
        }

        public void SaveData()
        {
            var userDataJson = Serialization.ToJson(userData);
            File.WriteAllText(path: Path.Combine(GetPluginUserDataPath(), "data.json"), contents: userDataJson);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new SteamVersionCheckerSettingsView();
        }
    }
}
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace PlayToEarn;

public partial class Validator
{
    [GeneratedRegex(@"^0x[a-fA-F0-9]{40}$")]
    private static partial Regex ValidAddressRegex();

    public static bool ValidAddress(string address)
        => ValidAddressRegex().IsMatch(address);
}

public partial class PlayToEarnModSystem : ModSystem
{
    private long lastTimestamp = 0;
    private readonly List<IServerPlayer> onlinePlayers = [];

    /// <summary>
    ///  Player UID / bool for earning coing
    /// </summary>
    public static readonly Dictionary<string, bool> playersWalletsStatus = [];

    public override void AssetsLoaded(ICoreAPI api)
    {
        base.AssetsLoaded(api);
        Debug.LoadLogger(api.Logger);
        Configuration.UpdateBaseConfigurations(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);

        api.Event.RegisterGameTickListener(OnTick, Configuration.millisecondsPerTick);

        api.ChatCommands.Create("wallet")
            .WithDescription("Set up your wallet address, /wallet 0x123... to start earning PTE")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .WithArgs(new StringArgParser("arguments", false))
            .HandleWith(SetWalletAddress);

        api.ChatCommands.Create("balance")
            .WithDescription("View your PTE balance to earn, ensure you have set your wallet using /wallet 0x123...")
            .RequiresPlayer()
            .RequiresPrivilege(Privilege.chat)
            .HandleWith(ViewBalance);

        api.Event.PlayerNowPlaying += PlayerJoin;
        api.Event.PlayerDisconnect += PlayerDisconnect;
    }

    #region commands
    private TextCommandResult ViewBalance(TextCommandCallingArgs args)
    {
        IServerPlayer player = args.Caller.Player as IServerPlayer;
        string statusText;
        if (PlayerAFK(player)) statusText = ", YOU ARE NOT EARNING PTE";
        else statusText = ", Currently earning PTE";

        Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("from", Configuration.httpFrom);
                var response = await client.GetAsync($"http://{Configuration.httpIp}/getbalance?uniqueid={player.PlayerUID}");
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"PTE: {content}{statusText}", EnumChatType.Notification);
            }
            catch (HttpRequestException ex)
            {
                if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, "You don't have any wallet set up", EnumChatType.CommandError);
                }
                else
                {
                    player.SendMessage(GlobalConstants.GeneralChatGroup, $"Failed to get balance, error code: {ex.StatusCode}", EnumChatType.CommandError);
                }
            }
        });

        return TextCommandResult.Success();
    }

    private TextCommandResult SetWalletAddress(TextCommandCallingArgs args)
    {
        if (args[0] == null) return TextCommandResult.Error($"No wallet provided", "5");

        IServerPlayer player = args.Caller.Player as IServerPlayer;
        string address = args[0].ToString();

        if (!Validator.ValidAddress(address))
            return TextCommandResult.Error($"Invalid wallet", "1");

        Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("from", Configuration.httpFrom);
                var body = new
                {
                    uniqueid = player.PlayerUID,
                    wallet = address,
                };
                var json = System.Text.Json.JsonSerializer.Serialize(body);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PutAsync($"http://{Configuration.httpIp}/updatewallet", content);
                response.EnsureSuccessStatusCode();

                player.SendMessage(GlobalConstants.GeneralChatGroup, "Success changing the wallet", EnumChatType.Notification);
            }
            catch (HttpRequestException ex)
            {
                player.SendMessage(GlobalConstants.GeneralChatGroup, $"Failed to set wallet, error code: {ex.StatusCode}", EnumChatType.CommandError);
            }
        });

        return TextCommandResult.Success();
    }
    #endregion

    #region coin give per gameplay
    private void OnTick(float obj)
    {
        // Running on secondary thread to not struggle the server
        Task.Run(() =>
        {
            try
            {
                // No players? nothing to do
                if (onlinePlayers.Count == 0)
                {
                    Debug.LogDebug("No players online...");
                    lastTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    return;
                }

                long actualTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int secondsPassed = (int)(actualTimestamp - lastTimestamp);
                BigInteger additionalCoins = Configuration.coinsPerSecond * secondsPassed;
                lastTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Adding additionalCoins to the wallets address
                foreach (IServerPlayer player in onlinePlayers)
                {
                    // Afk players
                    if (PlayerAFK(player))
                    {
                        Debug.LogDebug("Ignoring " + player.PlayerName + " because he is afk");
                        continue;
                    }

                    Task.Run(async () =>
                    {
                        try
                        {
                            using var client = new HttpClient();
                            client.DefaultRequestHeaders.Add("from", Configuration.httpFrom);
                            var body = new
                            {
                                uniqueid = player.PlayerUID,
                                quantity = additionalCoins.ToString(),
                            };
                            var json = JsonSerializer.Serialize(body);
                            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                            var response = await client.PutAsync($"http://{Configuration.httpIp}/increment", content);
                            response.EnsureSuccessStatusCode();

                            Debug.LogDebug($"{player.PlayerName} received: {Configuration.FormatCoinToHumanReadable(additionalCoins)} PTE");
                        }
                        catch (HttpRequestException ex)
                        {
                            Debug.LogDebug($"{player.PlayerName} cannot increment because: {ex.StatusCode}");
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"ERROR: Cannot increment wallet values, reason: {ex.Message}");
            }
        });
    }
    #endregion

    #region login events
    async private void PlayerJoin(IServerPlayer byPlayer)
    {
        onlinePlayers.Add(byPlayer);

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("from", Configuration.httpFrom);
            var body = new
            {
                uniqueid = byPlayer.PlayerUID
            };
            var json = System.Text.Json.JsonSerializer.Serialize(body);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"http://{Configuration.httpIp}/register", content);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception) { }
    }
    private void PlayerDisconnect(IServerPlayer byPlayer)
    {
        onlinePlayers.Remove(byPlayer);
    }
    #endregion

    #region utils
    private static bool PlayerAFK(IServerPlayer byPlayer)
    {
        if (AFKModule.Modules.Events.ModulesSoftAFK.TryGetValue(byPlayer.PlayerUID, out List<string> modulesAFK))
        {
            if (modulesAFK.Contains("Moviment")) return true;
            if (modulesAFK.Contains("Death")) return true;
            if (modulesAFK.Contains("Camera")) return true;
        }

        return false;
    }
    #endregion

    public class Debug
    {
        static private ILogger logger;

        static public void LoadLogger(ILogger _logger) => logger = _logger;
        static public void Log(string message)
        {
            logger?.Log(EnumLogType.Notification, $"[PlayToEarn] {message}");
        }
        static public void LogDebug(string message)
        {
            if (Configuration.enableExtendedLog)
                logger?.Log(EnumLogType.Debug, $"[PlayToEarn] {message}");
        }
        static public void LogWarn(string message)
        {
            logger?.Log(EnumLogType.Warning, $"[PlayToEarn] {message}");
        }
        static public void LogError(string message)
        {
            logger?.Log(EnumLogType.Error, $"[PlayToEarn] {message}");
        }
    }
}

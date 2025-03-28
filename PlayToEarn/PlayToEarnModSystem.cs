using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AFKModule.Modules;
using MySqlConnector;
using Vintagestory.API.Common;
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

    private static MySqlConnection walletsDatabase;

    public override void AssetsLoaded(ICoreAPI api)
    {
        base.AssetsLoaded(api);
        Debug.LoadLogger(api.Logger);
        Configuration.UpdateBaseConfigurations(api);
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);

        walletsDatabase = new MySqlConnection(
            $"Server={Configuration.databaseAddress};Database={Configuration.databaseName};User={Configuration.databaseUsername};Password={Configuration.databasePassword};"
        );

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
        IPlayer player = args.Caller.Player;
        string statusText;
        if (Events.playersSoftAfk.Contains(player.PlayerUID)) statusText = ", YOU ARE NOT EARNING PTE";
        else statusText = ", Currently earning PTE";

        walletsDatabase.Open();
        using MySqlCommand databaseCommand = new("SELECT value FROM vintagestory WHERE uniqueid = @playerUID", walletsDatabase);
        databaseCommand.Parameters.AddWithValue("@playerUID", player.PlayerUID);
        using MySqlDataReader reader = databaseCommand.ExecuteReader();
        if (reader.HasRows)
            while (reader.Read())
            {
                decimal value = reader.GetDecimal("value");
                walletsDatabase.Close();
                return TextCommandResult.Success($"PTE: {Configuration.FormatCoinToHumanReadable(value.ToString())}{statusText}", "3");
            }
        else
        {
            walletsDatabase.Close();
            return TextCommandResult.Error($"You don't have any wallet set up", "4");
        };
        walletsDatabase.Close();
        return TextCommandResult.Error($"You don't have any balance", "5");
    }

    private TextCommandResult SetWalletAddress(TextCommandCallingArgs args)
    {
        if (args[0] == null) return TextCommandResult.Error($"No wallet provided", "5");

        IPlayer player = args.Caller.Player;
        string address = args[0].ToString();

        if (!Validator.ValidAddress(address))
            return TextCommandResult.Error($"Invalid wallet", "1");

        // Update if exist
        {
            walletsDatabase.Open();
            using MySqlCommand databaseCommand = new("UPDATE vintagestory SET walletaddress = @address WHERE uniqueid = @playerUID", walletsDatabase);
            databaseCommand.Parameters.AddWithValue("@address", address);
            databaseCommand.Parameters.AddWithValue("@playerUID", player.PlayerUID);

            int rowsAffected = databaseCommand.ExecuteNonQuery();
            walletsDatabase.Close();
            if (rowsAffected > 0) return TextCommandResult.Success($"Wallet Set!", "2");
        }

        // Create if not exist
        {
            walletsDatabase.Open();
            using MySqlCommand databaseCommand = new("INSERT INTO vintagestory (walletaddress, uniqueid) VALUES (@address, @playerUID)", walletsDatabase);
            databaseCommand.Parameters.AddWithValue("@address", address);
            databaseCommand.Parameters.AddWithValue("@playerUID", player.PlayerUID);

            int rowsAffected = databaseCommand.ExecuteNonQuery();
            walletsDatabase.Close();
            if (rowsAffected > 0) return TextCommandResult.Success($"Wallet Set!", "2");
            else return TextCommandResult.Error($"Cannot set your wallet, contact the administrator", "4");
        }
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
                    if (Configuration.enableExtendedLog)
                        Debug.Log("No players online...");
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
                    if (Events.playersSoftAfk.Contains(player.PlayerUID))
                    {
                        if (Configuration.enableExtendedLog)
                            Debug.Log("Ignoring " + player.PlayerName + " because he is afk");
                        continue;
                    }

                    walletsDatabase.Open();
                    using MySqlCommand databaseCommand = new("UPDATE vintagestory SET value = value + @additionalCoins WHERE uniqueid = @playerUID", walletsDatabase);
                    databaseCommand.Parameters.AddWithValue("@additionalCoins", additionalCoins);
                    databaseCommand.Parameters.AddWithValue("@playerUID", player.PlayerUID);

                    int rowsAffected = databaseCommand.ExecuteNonQuery();
                    walletsDatabase.Close();
                    if (Configuration.enableExtendedLog)
                        if (rowsAffected > 0) Debug.Log($"{player.PlayerName} received: {Configuration.FormatCoinToHumanReadable(additionalCoins)} PTE");
                        else Debug.Log($"{player.PlayerName} does not have a wallet setup");
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"ERROR: Cannot increment wallet values, reason: {ex.Message}");
            }
        });
    }
    #endregion

    #region login events
    async private void PlayerJoin(IServerPlayer byPlayer)
    {
        onlinePlayers.Add(byPlayer);

        while (true)
        {
            try
            {
                walletsDatabase.Open();
                MySqlCommand databaseCommand = new("SELECT COUNT(*) FROM vintagestory WHERE uniqueid = @playerUID", walletsDatabase);
                databaseCommand.Parameters.AddWithValue("@playerUID", byPlayer.PlayerUID);

                int count = Convert.ToInt32(databaseCommand.ExecuteScalar());

                // Check if player is already registered
                if (count == 0)
                {
                    // In this case is not registered, so we will pre register the player
                    databaseCommand = new("INSERT INTO vintagestory (uniqueid) VALUES (@playerUID)", walletsDatabase);
                    databaseCommand.Parameters.AddWithValue("@playerUID", byPlayer.PlayerUID);

                    int rowsAffected = databaseCommand.ExecuteNonQuery();
                    if (rowsAffected == 0) Debug.Log($"ERROR: Cannot pre register player {byPlayer.PlayerName} for some reason");
                }
                walletsDatabase.Close();
                break;
            }
            catch (Exception ex)
            {
                walletsDatabase.Close();
                if (byPlayer.ConnectionState == EnumClientState.Connected ||
                    byPlayer.ConnectionState == EnumClientState.Playing)
                {
                    Debug.Log($"ERROR: cannot register {byPlayer.PlayerName}, reason: {ex.Message}");
                    await Task.Delay(1000);
                }
                else break;
            }
        }
    }
    private void PlayerDisconnect(IServerPlayer byPlayer)
    {
        onlinePlayers.Remove(byPlayer);
    }
    #endregion

    static public class Debug
    {
        static private ILogger _logger;
        static public void LoadLogger(ILogger logger) => _logger = logger;
        static public void Log(string message)
            => _logger?.Log(EnumLogType.Debug, $"[PlayToEarn] {message}");
    }
}

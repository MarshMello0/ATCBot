﻿using ATCBot.Structs;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace ATCBot
{
    class LobbyHandler
    {
        public TimeSpan delay = TimeSpan.FromSeconds(Program.config.delay);
        public TimeSpan steamTimeout = TimeSpan.FromSeconds(Program.config.steamTimeout);

        public List<VTOLLobby> vtolLobbies = new();
        public List<JetborneLobby> jetborneLobbies = new();

        public Program program;

        /// <summary>
        /// Whether or not the Steam client. is currently logged in.
        /// </summary>
        public static bool loggedIn;

        /// <summary>
        /// Whether or not we have tried logging in to Steam yet.
        /// </summary>
        public static bool triedLoggingIn;

        private SteamClient client;
        private CallbackManager manager;
        private SteamUser user;
        private SteamMatchmaking matchmaking;

        public async Task QueryTimer()
        {
            if (Program.shouldUpdate)
            {
                if(triedLoggingIn) Program.LogInfo("Updating lobbies...", "Lobby Handler");
                manager.RunWaitCallbacks(steamTimeout);
                await GetLobbies();
                await program.UpdateLobbyInformation();
            }
            else if (triedLoggingIn) Program.LogInfo("Skipping update...", "Lobby Handler");
            await Task.Delay(delay);
            await QueryTimer();
        }

        public LobbyHandler(Program program)
        {
            this.program = program;
            SetupSteam();
        }

        private void SetupSteam()
        {
            client = new SteamClient();
            manager = new CallbackManager(client);
            matchmaking = client.GetHandler<SteamMatchmaking>();
            user = client.GetHandler<SteamUser>();

            SetupCallbacks();

            Program.LogInfo("Setting up Steam connection...");
            client.Connect();
        }

        private void SetupCallbacks()
        {
            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Program.LogInfo($"Connected to Steam API. Logging into {SteamConfig.Config.SteamUserName}.", "Lobby Handler");

            byte[] sentryHash = null;
            string sentryPath = Path.Combine(Directory.GetCurrentDirectory(), "sentry.bin");
            if (File.Exists(sentryPath))
            {
                byte[] sentryFile = File.ReadAllBytes("sentry.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            user.LogOn(new SteamUser.LogOnDetails()
            {
                Username = SteamConfig.Config.SteamUserName,
                Password = SteamConfig.Config.SteamPassword,
                AuthCode = SteamConfig.Config.AuthCode,
                TwoFactorCode = SteamConfig.Config.TwoFactorAuthCode,
                SentryFileHash = sentryHash
            });
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Program.LogWarning("Disconnected from Steam! This usually means a problem with Steam's servers!", "Lobby Handler", true);
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool hasSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool hasF2A = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            triedLoggingIn = true;

            if (hasSteamGuard)
            {
                Program.LogWarning($"Looks like Steam does not trust this machine yet. " +
                    $"You have been emailed an auth code, please input that into steam.json and re-run the program.", "Lobby Handler", true);
                Environment.Exit(1);
            }

            if (hasF2A)
            {
                Program.LogWarning("Looks like you have Steam Guard enabled, please enter the current code into steam.json and re-run the program.", "Lobby Handler", true);
                Environment.Exit(1);
            }

            if (callback.Result != EResult.OK)
            {
                Program.LogWarning($"Failed to log into Steam because: {callback.Result} {callback.ExtendedResult}", "Lobby Handler", true);
                Environment.Exit(1);
            }

            Program.LogInfo("Logged into Steam account!", "Lobby Handler", true);
            loggedIn = true;
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Program.LogWarning("Logged out of Steam!", "Lobby Handler", true);
            loggedIn = false;
        }

        private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            Program.LogInfo("Updating sentryfile...");

            // write out our sentry file
            // ideally we'd want to write to the filename specified in the callback
            // but then this sample would require more code to find the correct sentry file to read during logon
            // for the sake of simplicity, we'll just use "sentry.bin"

            int fileSize;
            byte[] sentryHash;
            using (var fs = File.Open("sentry.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.Seek(callback.Offset, SeekOrigin.Begin);
                fs.Write(callback.Data, 0, callback.BytesToWrite);
                fileSize = (int)fs.Length;

                fs.Seek(0, SeekOrigin.Begin);
                using (var sha = SHA1.Create())
                {
                    sentryHash = sha.ComputeHash(fs);
                }
            }

            // inform the steam servers that we're accepting this sentry file
            user.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            Program.LogInfo("Done! You shouldn't need to re-authorize this computer again (unless you have Steam Guard enabled)!");
        }

        private async Task GetLobbies()
        {
            if (!triedLoggingIn) return;

            if (!loggedIn)
            {
                Program.LogWarning("Not logged into Steam, can't fetch lobby information!");
                return;
            }

            vtolLobbies.Clear();
            jetborneLobbies.Clear();

            VTOLLobby.passwordLobbies = 0;


            vtolLobbies.AddRange((await matchmaking.GetLobbyList(Program.vtolID)).Lobbies.Select(lobby => new VTOLLobby(lobby)).Where(lobby => !lobby.Equals(VTOLLobby.Empty)));
            jetborneLobbies.AddRange((await matchmaking.GetLobbyList(Program.jetborneID)).Lobbies.Select(lobby => new JetborneLobby(lobby)).Where(lobby => !lobby.Equals(JetborneLobby.Empty)));
        }
    }
}

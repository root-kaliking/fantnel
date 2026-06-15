using System;
using System.Text.Json;
using System.Threading.Tasks;
using Nirvana.Cipher.Entities.Yggdrasil;
using Nirvana.Cipher.Yggdrasil;
using Nirvana.Common.Entities;
using Nirvana.Common.Entities.Login;
using Nirvana.WPFLauncher.Http;
using Nirvana.WPFLauncher.Protocol;
using Serilog;

namespace Nirvana.Cipher.Cipher.Nirvana.Connection;

public static class NetEaseConnection {
    // 认证失败 后通过 涅槃云 认证
    public static bool IsServerAuthenticated = true;

    public static void CreateAuthenticator(string serverId, string gameId, string gameVersion, string modInfo, EntityUserInfo userInfo, Action<bool> handle)
    {
        Task.Run(() => {
            try {
                CreateAuthenticatorAsync(serverId, gameId, gameVersion, modInfo, userInfo, handle).GetAwaiter().GetResult();
            } catch (Exception) {
                // ignored
            }
        }).GetAwaiter().GetResult();
    }

    public static async Task CreateAuthenticatorAsync(string serverId, string gameId, string gameVersion, string modInfo, EntityUserInfo userInfo, Action<bool> handle)
    {
        var pair = Md5Mapping.GetMd5FromGameVersion(gameVersion);
        var success = await CreateAuthenticatorAsync(new GameProfile {
            GameId = gameId,
            GameVersion = gameVersion,
            BootstrapMd5 = pair.BootstrapMd5,
            DatFileMd5 = pair.DatFileMd5,
            Mods = JsonSerializer.Deserialize<ModList>(modInfo),
            User = new UserProfile {
                User = userInfo
            }
        }, serverId);
        handle.Invoke(success);
    }

    public static void CreateAuthenticator(GameProfile gameProfile, string serverId)
    {
        CreateAuthenticatorAsync(gameProfile, serverId).GetAwaiter().GetResult();
    }

    private static async Task<bool> CreateAuthenticatorAsync(GameProfile gameProfile, string serverId)
    {
        Exception? exception;
        try {
            X19.GetCrcSalt();
            Log.Warning("[Authentication] Joining Server: {0}", serverId);
            await StandardYggdrasil.JoinServerAsync(gameProfile, serverId);
            Log.Information("[Authentication] Success!");
            return true;
        } catch (Exception e) {
            exception = e;
        }

        if (IsServerAuthenticated) {
            Log.Warning("[Authentication] Authenticating Server: {0}", serverId);
            var data = await X19Extensions.Nirvana.ApiAsync<EntityResponseBase>($"/api/fantnel/authenticated?id={serverId}", gameProfile);
            if (data == null) {
                Log.Error("[Authentication]: {0}", JsonSerializer.Serialize(gameProfile));
                Log.Error("[Authentication]: Error!");
                return false;
            }

            if (data.Code == 1) {
                Log.Information("[Authentication] Success!");
                return true;
            }

            Log.Information("[Authentication] Failed: {0}", data.Message);
        }

        Log.Error("[Authentication]: {0}", JsonSerializer.Serialize(gameProfile));
        Log.Error("[Authentication] Failed: {0}", exception.Message);
        throw exception;
        // return false;
    }
}
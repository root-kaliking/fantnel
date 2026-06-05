using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Nirvana.Common;
using Nirvana.Common.Utils;
using Nirvana.Game.Launcher.Entities;
using Nirvana.Game.Launcher.Utils;
using Nirvana.WPFLauncher.Entities.WPFLauncher.Launch;
using Nirvana.WPFLauncher.Entities.WPFLauncher.NetGame.GameLaunch.Texture;
using Nirvana.WPFLauncher.Http;
using Nirvana.WPFLauncher.Utils;
using Nirvana.WPFLauncher.Utils.Cipher;
using Serilog;
using TokenUtil = Nirvana.WPFLauncher.Utils.Cipher.TokenUtil;

namespace Nirvana.Game.Launcher.Services.Java;

public class CommandService {
    private static readonly JsonSerializerOptions Options = new() {
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions Options1 = new() {
        // 关键设置：使用不转义的编码器
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<EntityJavaFile> _minecraft = []; // path, url
    private readonly string _version;
    private readonly string _versionPath;

    private string _cmd = "";
    private string _nativesPath = string.Empty;
    public required EnumGameVersion GameVersion;
    public required EntityLaunchGame LauncherGame;
    public required string ProtocolVersion;
    public required int RpcPort = 11413;
    public required int SocketPort;
    public required string Uuid;
    public required string WorkPath;

    public CommandService()
    {
        _version = GameVersionUtil.GetGameVersionFromEnum(GameVersion);
        _versionPath = Path.Combine(PathUtil.GameBaseMcPath, "versions", _version);

        // windows 不需要修复 lwjgl
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            // 获取原版信息
            var minecraft = X19Extensions.Bmcl.Api<Dictionary<string, JsonElement>>($"/version/{_version}/json");
            if (minecraft != null) {
                _minecraft = BuildJarListBase(minecraft);
            } else {
                Log.Error("BmclApi returned null, version: {0}", _version);
            }
        }
    }

    public CommandService Init()
    {
        var versionJson = Path.Combine(_versionPath, _version + ".json");
        if (!File.Exists(versionJson)) {
            throw new Exception("Game version JSON not found, please go to Setting to fix the game file and try again.");
        }

        var cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(options: Options, json: File.ReadAllText(versionJson));

        if (cfg == null) {
            throw new Exception("Game version JSON deserialize failed.");
        }

        BuildCommand(cfg, _version, SocketPort);
        // 修复 natives
        InstallNatives().GetAwaiter().GetResult();
        // 安装 验证库
        InstallNativeDll();

        // 保存到文件，方便调试
        var scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "command" + PathUtil.ScriptSuffix);
        Tools.SaveShellScript(scriptPath, GetJavaCommand()).GetAwaiter().GetResult();

        var optionsPath = Path.Combine(WorkPath, "options.txt");
        if (!File.Exists(optionsPath)) {
            File.WriteAllText(optionsPath, "guiScale:2\nlang:zh_cn\nmaxFps:120\n");
        }

        return this;
    }

    private async Task InstallNatives()
    {
        if (_minecraft.Count == 0) {
            return;
        }

        // 删除 linux/mac 下的 natives[win库]
        if (Directory.Exists(_nativesPath)) {
            Directory.Delete(_nativesPath, true);
        }

        // 解压 natives 库
        foreach (var item in _minecraft.Where(item => item.IsNative())) {
            Log.Warning("Fix Native Extract {0}", item.GetPath1());
            if (item.DownloadAuto()) {
                await CompressionUtil.ExtractAsync(item.GetPath(), _nativesPath);
            }
        }
    }

    private void InstallNativeDll()
    {
        try {
            var path = Path.Combine(PathUtil.ResourcePath, "api-ms-win-crt-utility-l1-1-1.dll");
            var runtimePath = Path.Combine(_nativesPath, "runtime");
            FileUtil.CopyFileSafe(path, Path.Combine(runtimePath, "api-ms-win-crt-utility-l1-1-1.dll"));
        } catch (Exception ex) {
            Log.Error("Failed to install native dll: {0}", ex);
        }
    }

    // 生成启动参数 【独立运行】
    private string GetJavaCommand()
    {
        return "cd \"" + WorkPath + "\"" + "\n" + GetJavaPath(GameVersion) + _cmd;
    }

    public Process? StartGame()
    {
        var javaPath = GetJavaPath(GameVersion);
        FileUtil.SetUnixFilePermissions(javaPath); // 添加 Java 权限
        return Process.Start(new ProcessStartInfo(javaPath, _cmd) {
            UseShellExecute = false,
            WorkingDirectory = WorkPath
        });
    }

    private static string GetJavaPath(EnumGameVersion gameVersion)
    {
        var javaPath = gameVersion switch {
            >= EnumGameVersion.V_1_20_6 => PathUtil.Jre21Path,
            >= EnumGameVersion.V_1_16 => PathUtil.Jre17Path,
            _ => PathUtil.Jre8Path
        };
        return Path.Combine(javaPath, "bin", PathUtil.JavaExePath);
    }


    private static bool CheckRules(JsonElement item)
    {
        // rules
        if (item.TryGetProperty("rules", out var rulesElement)) {
            foreach (var ruleItem in rulesElement.EnumerateArray()) {
                if (!ruleItem.TryGetProperty("action", out var actionElement)) {
                    continue;
                }

                if (!ruleItem.TryGetProperty("os", out var osElement)) {
                    continue;
                }

                if (!osElement.TryGetProperty("name", out var nameElement)) {
                    continue;
                }

                var action = actionElement.GetString();
                var name = nameElement.GetString(); // 系统名称

                switch (action) {
                    case "allow":
                        return GetRunOs().Any(name1 => name1.Equals(name));
                    case "disallow":
                        return !GetRunOs().Any(name1 => name1.Equals(name));
                }
            }
        }

        return true;
    }

    private static List<EntityJavaFile> BuildJarListBase(Dictionary<string, JsonElement> cfg)
    {
        // path, url
        var jarList = new List<EntityJavaFile>();

        if (!cfg.TryGetValue("libraries", out var libElement)) {
            throw new Exception("libraries not found");
        }

        foreach (var item in libElement.EnumerateArray().Where(CheckRules)) {
            // downloads
            if (!item.TryGetProperty("downloads", out var downElement)) {
                continue;
            }

            var name = string.Empty;
            if (item.TryGetProperty("name", out var nameElement)) {
                name = nameElement.GetString();
            }

            // artifact
            if (downElement.TryGetProperty("artifact", out var artiElement)) {
                if (artiElement.TryGetProperty("path", out var pathElement)) {
                    var path = pathElement.GetString();
                    if (path != null) {
                        path = Path.Combine("libraries", path);
                        jarList = AddJarList(jarList, path, artiElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : string.Empty, name);
                    }
                }
            }

            // classifiers
            if (downElement.TryGetProperty("classifiers", out var classElement)) {
                if (item.TryGetProperty("natives", out var natives)) {
                    foreach (var osName1 in GetRunOs()) {
                        if (natives.TryGetProperty(osName1, out var nativeElement)) {
                            // "windows": "natives-windows"
                            var osName = nativeElement.GetString(); // natives-windows
                            if (string.IsNullOrEmpty(osName)) {
                                continue;
                            }

                            var runArch = GetRunArch();
                            foreach (var archName in runArch) {
                                var osNameArch = osName.Replace("${arch}", archName);
                                if (classElement.TryGetProperty(osNameArch, out var nativesElement)) {
                                    if (nativesElement.TryGetProperty("path", out var path1Element)) {
                                        var path = path1Element.GetString();
                                        if (path != null) {
                                            path = Path.Combine("libraries", path);
                                            jarList = AddJarList(jarList, path, nativesElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : string.Empty, name, true);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return jarList;
    }

    private static string[] GetRunArch()
    {
        return RuntimeInformation.ProcessArchitecture switch {
            // Architecture.Arm64 => ["aarch_64"],
            // Architecture.Arm => ["aarch_64"],
            // Architecture.Armv6 => ["aarch_64"],
            Architecture.X86 => ["32"],
            Architecture.X64 => ["64"],
            _ => ["aarch_64"]
        };
    }

    private static List<EntityJavaFile> AddJarList(List<EntityJavaFile> jarList, string path, string? url, string? name, bool isNative = false)
    {
        // 优先采用已存在url的jar
        foreach (var jar in jarList.Where(jar => jar.Equals(path)).ToList()) {
            // 已存在的值为空，移除
            if (string.IsNullOrEmpty(jar.Url)) {
                jarList.Remove(jar);
            } else {
                // 已存在的值不为空，跳过
                return jarList;
            }
        }

        jarList.Add(new EntityJavaFile(path, url, name, isNative));
        return jarList;
    }

    private static List<string> BuildJarListsByName(Dictionary<string, JsonElement> cfg)
    {
        var jarList = new List<string>();
        if (cfg.TryGetValue("libraries", out var value)) {
            foreach (var item in value.EnumerateArray()) {
                // 获取名称
                if (!item.TryGetProperty("name", out var value2)) {
                    continue;
                }

                // 解析名称
                var name = value2.GetString();
                if (string.IsNullOrEmpty(name)) {
                    continue;
                }

                // org.lwjgl:lwjgl-openal:3.3.1 > org/lwjgl/lwjgl-openal/3.3.1/lwjgl-openal-3.3.1.jar
                // org.lwjgl:lwjgl-glfw:3.3.1:natives-windows-x86 > org/lwjgl/lwjgl-glfw/3.3.1/natives-windows-x86

                var parts = name.Split(':');
                if (parts.Length is < 3 or > 4) {
                    Log.Warning("Invalid name format: {0}", name);
                    continue;
                }

                var groupId = parts[0];
                var artifactId = parts[1];
                var version = parts[2];
                var classifier = parts.Length == 4 ? parts[3] : null;

                var groupPath = groupId.Replace('.', '/');

                // artifactId-version[-classifier].jar
                var fileName = classifier != null ? $"{artifactId}-{version}-{classifier}.jar" : $"{artifactId}-{version}.jar";

                jarList.Add(Path.Combine("libraries", groupPath, artifactId, version, fileName));
            }
        }

        return jarList;
    }

    private static List<EntityJavaFile> BuildJarLists(Dictionary<string, JsonElement> cfg, string version)
    {
        // path, url
        var jarList = BuildJarListBase(cfg);

        // 修复 1.8.9 奇葩 libraries
        foreach (var jar in BuildJarListsByName(cfg).Where(jar => jarList.All(item => !item.Equals(jar)))) {
            jarList.Add(new EntityJavaFile(jar));
        }

        // \versions\1.8.9\1.8.9.jar
        var verValue = Path.Combine("versions", version, version + ".jar");
        jarList.Add(new EntityJavaFile(verValue));

        // 自动处理所需库
        return jarList.Where(item => item.DownloadAuto()).ToList();
    }

    public static string[] GetRunOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return ["windows"];
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            return ["osx", "macos"];
        }

        return ["linux"];
    }

    private void BuildCommand(Dictionary<string, JsonElement> cfg, string version, int socketPort)
    {
        var jvmArguments = "";
        if (cfg.TryGetValue("jvm_arguments", out var jvmArguments1)) {
            jvmArguments = jvmArguments1.GetString();
        }

        var classPaths = BuildJarLists(cfg, _version);

        var nativesPath = Path.Combine("versions", _version, "natives");

        if (!string.IsNullOrEmpty(jvmArguments)) {
            // 修复 linux/mac 冲突
            nativesPath = GameArgumentsUtil.GetArguments("java.library.path", jvmArguments, false, CommandMode.Mode5, CommandMode.Mode6);
            jvmArguments = GameArgumentsUtil.DeleteArguments("java.library.path", jvmArguments, CommandMode.Mode5, CommandMode.Mode6);
            // 修复 模块路径 错误
            jvmArguments = ReplaceLib("p", jvmArguments);
            // // 修复 linux/mac 冲突
            // if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)){
            //     // 避免 linux 使用 win 库
            //     genClassPath = true;
            //     jvmArguments = DeleteArguments("cp", jvmArguments);
            // }
            jvmArguments = ReplaceLib("cp", jvmArguments);

            // 而外 lib 路径
            var classPath1 = GameArgumentsUtil.GetArguments("cp", jvmArguments, false, CommandMode.Mode12, CommandMode.Mode13);
            var classPathList = classPath1.Split(PathUtil.PathSeparator);

            // 过滤 重复路径
            foreach (var path in classPathList) {
                if (string.IsNullOrEmpty(path)) {
                    continue;
                }

                var isAdd = classPaths.All(classPath => !classPath.Contains(path));
                if (isAdd) {
                    classPaths.Add(new EntityJavaFile(path));
                }
            }

            classPaths = FilterFile(classPaths);
            jvmArguments = GameArgumentsUtil.UpdateArguments("cp", string.Join(PathUtil.PathSeparator, EntityJavaFile.ToList(classPaths)), jvmArguments, CommandMode.Mode12, CommandMode.Mode13);
        }

        if (string.IsNullOrEmpty(jvmArguments)) {
            jvmArguments ??= "";
            jvmArguments = GameArgumentsUtil.UpdateArguments("cp", string.Join(PathUtil.PathSeparator, EntityJavaFile.ToList(classPaths)), jvmArguments, CommandMode.Mode12, CommandMode.Mode13);
        }

        if (cfg.TryGetValue("mainClass", out var mainClassElement)) {
            var mainClass = mainClassElement.GetString();
            // mainClass 不是空的 | 参数没有包含 mainClass
            if (!string.IsNullOrEmpty(mainClass) && !jvmArguments.Contains(mainClass)) {
                jvmArguments = GameArgumentsUtil.AddArguments(jvmArguments, mainClass); // 添加 修复参数
            }
        }

        jvmArguments = jvmArguments.Replace("${library_directory}", Path.Combine(PathUtil.GameBaseMcPath, "libraries"));
        jvmArguments = GameArgumentsUtil.UpdateArguments("libraryDirectory", Path.Combine(PathUtil.GameBaseMcPath, "libraries"), jvmArguments, CommandMode.Mode5, CommandMode.Mode6);
        jvmArguments = GameArgumentsUtil.DeleteArguments("Xmx", jvmArguments, CommandMode.Mode14);

        // 添加 验证信息
        var stringBuilder = new StringBuilder().Append(" -Xmx").Append(NirvanaConfig.GetValue<int>("gameMemory")).Append("M ").Append(NirvanaConfig.GetValue<string>("jvmArgs")).Append($" -DlauncherControlPort={socketPort}").Append($" -DlauncherGameId={LauncherGame.GameId}").Append($" -DuserId={LauncherGame.Account.GetUserId()}").Append($" -DToken={TokenUtil.GenerateEncryptToken(LauncherGame.Account.GetToken())}").Append(" -DServer=RELEASE").Append(AddNativePath(nativesPath));

        jvmArguments = GameArgumentsUtil.AddArguments(stringBuilder.ToString(), jvmArguments); // 添加 修复参数

        // 启动参数
        var minecraftArguments = "";
        if (cfg.TryGetValue("parameter_arguments", out var minecraftArguments1)) {
            minecraftArguments = minecraftArguments1.GetString();
        }

        if (string.IsNullOrEmpty(minecraftArguments)) {
            minecraftArguments = cfg.GetValueOrDefault("minecraftArguments").GetString();
        }

        // 没有启动参数
        ArgumentException.ThrowIfNullOrEmpty(minecraftArguments);

        jvmArguments = GameArgumentsUtil.AddArguments(jvmArguments, BuildCommandExFix()); // 添加 修复参数

        minecraftArguments = GameArgumentsUtil.UpdateArguments("gameDir", "${game_directory}", minecraftArguments, CommandMode.Mode2, CommandMode.Mode3); // 修复错误内置
        minecraftArguments = GameArgumentsUtil.UpdateArguments("assetsDir", "${assets_root}", minecraftArguments, CommandMode.Mode2, CommandMode.Mode3); // 修复错误内置

        minecraftArguments = minecraftArguments.Replace("${game_directory}", WorkPath);
        minecraftArguments = minecraftArguments.Replace("--userType ${user_type}", string.Empty);
        minecraftArguments = minecraftArguments.Replace("${version_name}", version);
        minecraftArguments = minecraftArguments.Replace("${auth_player_name}", LauncherGame.RoleName);
        minecraftArguments = minecraftArguments.Replace("${auth_uuid}", Uuid);
        minecraftArguments = minecraftArguments.Replace("--versionType ${version_type}", string.Empty);
        minecraftArguments = minecraftArguments.Replace("${assets_root}", Path.Combine(PathUtil.GameBaseMcPath, "assets"));
        minecraftArguments = minecraftArguments.Replace("${assets_index_name}", version);

        minecraftArguments = minecraftArguments.Replace("${auth_access_token}", GameVersion >= EnumGameVersion.V_1_18 ? "0" : RandomUtil.GetRandomString(32, "ABCDEF0123456789"));

        minecraftArguments = GameArgumentsUtil.UpdateArguments("server", LauncherGame.ServerIp, minecraftArguments, CommandMode.Mode3, CommandMode.Mode13);
        minecraftArguments = GameArgumentsUtil.UpdateArguments("port", LauncherGame.ServerPort.ToString(), minecraftArguments, CommandMode.Mode3, CommandMode.Mode13);

        minecraftArguments = GameArgumentsUtil.UpdateArguments("userProperties", GetUserProperties(version), minecraftArguments, CommandMode.Mode3, CommandMode.Mode13);
        minecraftArguments = GameArgumentsUtil.UpdateArguments("userPropertiesEx", GetUserPropertiesEx(), minecraftArguments, CommandMode.Mode3, CommandMode.Mode13);

        _cmd = GameArgumentsUtil.AddArguments(jvmArguments, minecraftArguments);
    }

    private List<EntityJavaFile> FilterFile(List<EntityJavaFile> classPaths)
    {
        // 是 natives 文件，不用添加
        return _minecraft.Count <= 0 ? classPaths : classPaths.Where(classPath => !classPath.Contains("-natives-")).ToList();
    }

    // 修复 -cp 路径
    private string ReplaceLib(string name, string text)
    {
        var sourceText = GameArgumentsUtil.GetArguments1(name, text, CommandMode.Mode12, CommandMode.Mode13);
        if (string.IsNullOrEmpty(sourceText.Item1)) {
            return text;
        }

        var source = sourceText.Item2.Split(';');
        var combinedPaths = new StringBuilder();
        foreach (var pathSegment in source) {
            var fullPath = pathSegment.Replace(";", "");
            // fullPath = fullPath.Replace(":", "");
            fullPath = EntityJavaFile.FixPath(fullPath); // 有分割符

            var filePath = fullPath; // 不完整路径

            var fullPath1 = filePath; // 无分割符
            fullPath1 = Path.Combine(PathUtil.GameBaseMcPath, fullPath1);

            fullPath = fullPath1 + PathUtil.PathSeparator; // 修复 linux/mac 引用出错

            if (!File.Exists(fullPath1)) {
                Log.Error("File not found: {0}", fullPath1);
                continue;
            }

            // 是 native/lwjgl 文件，不用添加
            if (_minecraft.Count > 0) {
                if (EntityJavaFile.Contains("-natives", filePath)) {
                    Log.Warning("Fix Native Continue {0}", filePath);
                    continue;
                }

                if (EntityJavaFile.Contains("org/lwjgl/", filePath)) {
                    Log.Warning("Fix Lwjgl Continue {0}", filePath);
                    continue;
                }
            }

            combinedPaths.Append(fullPath);
        }

        if (_minecraft.Count == 0) {
            return text.Replace(sourceText.Item1, " -" + name + " \"" + combinedPaths + "\"");
        }

        // 修复 lwjgl
        foreach (var item in _minecraft.Where(item => item.StartsWith("org/lwjgl/"))) {
            Log.Warning("Fix Lwjgl Auto {0}", item.GetPath1());
            if (item.DownloadAuto()) {
                combinedPaths.Append(item.GetPathSeparator());
            }
        }

        return text.Replace(sourceText.Item1, " -" + name + " \"" + combinedPaths + "\"");
    }

    // 修复不同版本在不同系统出现问题
    private string BuildCommandExFix()
    {
        var stringBuilder = new StringBuilder();
        if (GameVersion > EnumGameVersion.V_1_12_2) {
            // Mac 高版本修复
            stringBuilder.Append("-XstartOnFirstThread ");
        }

        return stringBuilder.ToString();
    }

    private string AddNativePath(string nativesPath)
    {
        var natives = Path.Combine(PathUtil.GameBaseMcPath, nativesPath);
        _nativesPath = natives;

        // 避免 linux 出现权限问题
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            var linkPath = "/tmp/fantnel-natives-" + _version;
            // 创建 natives 目录符号链。
            Tools.CreateLinkDirectory(linkPath, natives);
            natives = linkPath;
        }

        var runtime = Path.Combine(natives, "runtime");
        return $" -Djava.library.path=\"{natives}\" -Druntime_path=\"{runtime}\" ";
    }

    private string GetUserPropertiesEx(EnumGType t = EnumGType.NetGame)
    {
        var jsonContent = JsonSerializer.Serialize(new EntityUserPropertiesEx {
            GameType = (int)t,
            Channel = "netease",
            TimeDelta = 0,
            IsFilter = true,
            LauncherVersion = ProtocolVersion
        });

        // 引号 转换成 \"
        return JsonSerializer.Serialize(jsonContent, Options1);
    }

    private string GetUserProperties(string version)
    {
        if (LauncherGame == null) {
            throw new Exception("No Launcher Game Found");
        }

        var format = version == "1.7.10" ? "\"uid\":[{0}],\"gameid\":[{1}],\"launcherport\":[{2}],\\\"filterkey\\\":[\\\"{3}\\\",\\\"0\\\"],\\\"filterpath\\\":[\\\"\\\",\\\"0\\\"],\\\"timedelta\\\":[0,0],\\\"launchversion\\\":[\\\"{3}\\\",\\\"0\\\"]" : "\\\"uid\\\":[{0},0],\\\"gameid\\\":[{1},0],\\\"launcherport\\\":[{2},0],\\\"filterkey\\\":[\\\"{3}\\\",\\\"0\\\"],\\\"filterpath\\\":[\\\"\\\",\\\"0\\\"],\\\"timedelta\\\":[0,0],\\\"launchversion\\\":[\\\"{4}\\\",\\\"0\\\"]";
        var args = new List<object> {
            LauncherGame.Account.GetUserId(), 0, RpcPort, RandomUtil.GetRandomString(32, "abcdefghijklmnopqrstuvwxyz"),
            ProtocolVersion
        };
        var text = string.Format(format, args.ToArray());
        return "\"{" + text + "}\"";
    }
}
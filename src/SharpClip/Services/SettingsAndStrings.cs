using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SharpClip;

internal sealed class AppSettings
{
    public string Language { get; set; } = "";
    public int HistoryLimit { get; set; } = 500;
    public bool PasteIntoActiveApp { get; set; } = true;
    public bool AlwaysPlainText { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool AnimatePanel { get; set; } = true;
    public bool Monitoring { get; set; } = true;
}

internal static class SettingsStore
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SharpClip",
        "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        Strings.Apply(Current.Language);
        StartupRegistration.SetEnabled(Current.LaunchAtLogin);
    }
}

internal static class Strings
{
    private static Dictionary<string, string> _map = English;

    public static string Get(string key) => _map.TryGetValue(key, out var value) ? value : English.GetValueOrDefault(key, key);

    public static void Apply(string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            language = CultureInfo.CurrentUICulture.Name switch
            {
                var name when name.StartsWith("ko", StringComparison.OrdinalIgnoreCase) => "ko",
                "zh-CN" or "zh-Hans" or "zh-SG" => "zh-Hans",
                _ => "en",
            };
        }
        _map = language switch
        {
            "ko" => Korean,
            "zh-Hans" => Chinese,
            _ => English,
        };
    }

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("", "System"),
        ("en", "English"),
        ("ko", "한국어"),
        ("zh-Hans", "简体中文"),
    ];

    private static readonly Dictionary<string, string> English = new()
    {
        ["search"] = "Search",
        ["history"] = "History",
        ["settings"] = "Settings",
        ["quit"] = "Quit",
        ["queue"] = "Clip Queue",
        ["paste"] = "Paste into the active app",
        ["plain"] = "Always paste as plain text",
        ["login"] = "Launch at login",
        ["limit"] = "History limit",
        ["language"] = "Language",
        ["monitor"] = "Clipboard monitoring",
        ["exclude"] = "Excluded apps",
        ["export"] = "Export",
        ["import"] = "Import",
        ["unlimited"] = "Unlimited",
    };

    private static readonly Dictionary<string, string> Korean = new()
    {
        ["search"] = "검색",
        ["history"] = "기록",
        ["settings"] = "설정",
        ["quit"] = "종료",
        ["queue"] = "클립 대기열",
        ["paste"] = "사용 중인 앱에 붙여넣기",
        ["plain"] = "항상 일반 텍스트로 붙여넣기",
        ["login"] = "로그인할 때 실행",
        ["limit"] = "기록 개수",
        ["language"] = "언어",
        ["monitor"] = "클립보드 감시",
        ["exclude"] = "제외한 앱",
        ["export"] = "내보내기",
        ["import"] = "가져오기",
        ["unlimited"] = "제한 없음",
    };

    private static readonly Dictionary<string, string> Chinese = new()
    {
        ["search"] = "搜索",
        ["history"] = "历史",
        ["settings"] = "设置",
        ["quit"] = "退出",
        ["queue"] = "剪贴队列",
        ["paste"] = "粘贴到当前应用",
        ["plain"] = "总是按纯文本粘贴",
        ["login"] = "登录时启动",
        ["limit"] = "历史条数",
        ["language"] = "语言",
        ["monitor"] = "剪贴板监控",
        ["exclude"] = "排除的应用",
        ["export"] = "导出",
        ["import"] = "导入",
        ["unlimited"] = "不限制",
    };
}

internal static class StartupRegistration
{
    private const string Name = "SharpClip";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null) return;
        if (enabled)
        {
            key.SetValue(Name, Environment.ProcessPath ?? "");
        }
        else
        {
            key.DeleteValue(Name, false);
        }
    }
}

internal static class ProtocolRegistration
{
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        foreach (var scheme in new[] { "sharpclip", "clipbara" })
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            key.SetValue("", "URL:SharpClip");
            key.SetValue("URL Protocol", "");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue("", $"\"{exe}\" \"%1\"");
        }
    }
}

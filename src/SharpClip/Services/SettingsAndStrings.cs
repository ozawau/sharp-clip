using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace SharpClip;

internal sealed class AppSettings
{
    public int SettingsVersion { get; set; }
    public bool WelcomeCompleted { get; set; }
    public string Language { get; set; } = "";
    public int HistoryLimit { get; set; } = 500;
    public bool PasteIntoActiveApp { get; set; } = true;
    public bool AlwaysPlainText { get; set; }
    public bool LaunchAtLogin { get; set; }
    public bool AnimatePanel { get; set; } = true;
    public bool Monitoring { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public string Theme { get; set; } = "system";
    public string HistoryTabColor { get; set; } = "";

    public bool PanelControl { get; set; } = true;
    public bool PanelShift { get; set; } = true;
    public bool PanelAlt { get; set; }
    public bool PanelWin { get; set; }
    public int PanelKey { get; set; } = 0x56;

    public bool QueueControl { get; set; }
    public bool QueueShift { get; set; }
    public bool QueueAlt { get; set; }
    public bool QueueWin { get; set; }
    public int QueueKey { get; set; }

    public bool ClearControl { get; set; } = true;
    public bool ClearShift { get; set; } = true;
    public bool ClearAlt { get; set; }
    public bool ClearWin { get; set; }
    public int ClearKey { get; set; } = 0x08;

    public int QuickLookKey { get; set; } = 0x20;
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
        if (Current.SettingsVersion < 2)
        {
            if (Current.QueueKey == 0x51 && Current.QueueControl && Current.QueueShift && !Current.QueueAlt && !Current.QueueWin)
            {
                Current.QueueKey = 0;
                Current.QueueControl = false;
                Current.QueueShift = false;
            }
            if (Current.ClearKey == 0x2E && Current.ClearControl && Current.ClearShift && !Current.ClearAlt && !Current.ClearWin)
            {
                Current.ClearKey = 0x08;
            }
            Current.SettingsVersion = 2;
            Save();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        StartupRegistration.SetEnabled(Current.LaunchAtLogin);
    }

    public static ElementTheme RequestedTheme() => Current.Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static bool SystemMotionEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    public static bool MotionAllowed() => Current.AnimatePanel && SystemMotionEnabled();
}

internal static class Strings
{
    private static Dictionary<string, string> _map = [];

    public static string Get(string key) => _map.TryGetValue(key, out var value) && value is not null ? value : English.GetValueOrDefault(key) ?? key;

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

    public static IReadOnlyList<(string Code, string Name)> Languages =>
    [
        ("", Get("lang_system")),
        ("en", "English"),
        ("ko", "한국어"),
        ("zh-Hans", "简体中文"),
    ];

    private static readonly Dictionary<string, string> English = new()
    {
        ["search"] = "Search clipboard...",
        ["type"] = "Type",
        ["type_n"] = "Type ({0})",
        ["date"] = "Date",
        ["all"] = "All",
        ["today"] = "Today",
        ["week"] = "Last 7 days",
        ["month"] = "Last 30 days",
        ["clear_filters"] = "Clear Filters",
        ["clear_type"] = "Clear Type Filter",
        ["new_board"] = "New Pinboard",
        ["clear_history"] = "Clear Clipboard History?",
        ["clear_history_body"] = "This deletes {0} unpinned clips from history. Pinboard items stay available.",
        ["clear"] = "Clear History",
        ["paste_fmt"] = "Paste with Formatting",
        ["paste_plain"] = "Paste as Plain Text",
        ["paste_one"] = "Paste",
        ["rename"] = "Rename",
        ["edit"] = "Edit Text",
        ["add_board"] = "Add to Pinboard",
        ["delete_clip"] = "Delete Clip",
        ["remove_board"] = "Remove from Pinboard",
        ["delete_board"] = "Delete Pinboard",
        ["delete_board_body"] = "This removes the pinboard only. The clips stay in clipboard history.",
        ["rename_board"] = "Rename Pinboard",
        ["create"] = "Create",
        ["cancel"] = "Cancel",
        ["save"] = "Save",
        ["name"] = "Name",
        ["chars"] = "{0} chars",
        ["chars_k"] = "{0}K chars",
        ["url_badge"] = "URL",
        ["file_badge"] = "File",
        ["empty_title"] = "Copy anything",
        ["empty_body"] = "Your clipboard history will appear here",
        ["no_results"] = "No Results",
        ["no_results_body"] = "Try a different search or filter",
        ["text"] = "Text",
        ["rich"] = "Rich Text",
        ["html"] = "HTML",
        ["image"] = "Image",
        ["link"] = "Link",
        ["file"] = "File",
        ["color"] = "Color",
        ["other"] = "Other",
        ["now"] = "now",
        ["secs"] = "{0}s ago",
        ["mins"] = "{0}m ago",
        ["hours"] = "{0}h ago",
        ["days"] = "{0}d ago",
        ["weeks"] = "{0}w ago",
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
        ["ok"] = "OK",
        ["general"] = "General",
        ["appearance"] = "Appearance",
        ["shortcuts"] = "Shortcuts",
        ["exclusions"] = "Exclusions",
        ["about"] = "About",
        ["pasting"] = "Pasting",
        ["paste_help"] = "Clicking a clip or pressing Enter pastes it where you were typing. When off, the clip is only copied.",
        ["paste_info"] = "After the panel closes, SharpClip presses Ctrl+V. If Windows will not bring that app forward, the clip is still copied.",
        ["plain_help"] = "Hold Shift while pasting to switch for a single paste.",
        ["plain_info"] = "Removes fonts, colors, and links from rich text and HTML clips, pasting only the text.",
        ["tray"] = "Show icon in the notification area",
        ["tray_hidden"] = "To open Settings, open SharpClip again or press Ctrl+, in the panel.",
        ["tray_hidden_title"] = "Tray icon hidden",
        ["tray_hidden_body"] = "SharpClip keeps running. To open Settings again, open SharpClip or press Ctrl+, in the panel.",
        ["backup"] = "Backup",
        ["export_help"] = "Export history and pinboards to a JSON file.",
        ["import_help"] = "Import a backup file. Existing clips are kept; duplicates are skipped.",
        ["export_ok"] = "Backup exported successfully.",
        ["export_fail"] = "Export failed: {0}",
        ["import_ok"] = "Imported {0} clips. Skipped {1} duplicates.",
        ["import_fail"] = "Import failed: {0}",
        ["theme"] = "Theme",
        ["theme_system"] = "System",
        ["theme_light"] = "Light",
        ["theme_dark"] = "Dark",
        ["animate"] = "Animate Panel",
        ["animate_help"] = "Slide the history panel in and out. When off, it appears and closes at once.",
        ["animate_reduced"] = "Off while animation effects are disabled in Windows Settings.",
        ["shortcut_panel"] = "Toggle History Panel",
        ["shortcut_panel_info"] = "Combine Ctrl, Alt, or the Windows key with another key, like Alt+V or Ctrl+Shift+P. Function keys (F1–F12) work on their own. Shift alone is not supported.",
        ["shortcut_queue"] = "Start or End Clip Queue",
        ["shortcut_queue_info"] = "While the queue is on, everything you copy joins it, and each Ctrl+V pastes the next item in order.",
        ["shortcut_queue_help"] = "Also available from the tray menu.",
        ["shortcut_clear"] = "Clear All History",
        ["shortcut_quick"] = "Quick Look Preview",
        ["shortcut_quick_info"] = "A single key with no modifiers. It only works while the panel is open, so it will not clash with other apps.",
        ["shortcut_quick_help"] = "Pressed inside the panel with a card selected.",
        ["tab_nav"] = "Tab Navigation",
        ["tab_history"] = "History",
        ["tab_boards"] = "First 8 Pinboards",
        ["tab_nav_help"] = "Fixed shortcuts in tab order. Only active while the history panel is open, including while searching.",
        ["press_shortcut"] = "Press shortcut…",
        ["press_key"] = "Press a key…",
        ["not_set"] = "Not set",
        ["shortcut_invalid"] = "Use Ctrl, Alt, or the Windows key, or a function key.",
        ["shortcut_taken"] = "That shortcut is already used.",
        ["shortcut_reserved"] = "That key is reserved for panel navigation.",
        ["shortcut_failed"] = "Windows could not register one of these shortcuts. Another app may already be using it.",
        ["key_space"] = "Space",
        ["exclude_help"] = "Excluded apps will not have their clipboard content saved.",
        ["about_version"] = "Version {0}",
        ["about_body"] = "Free, with no account and no telemetry.",
        ["lang_system"] = "System",
        ["today_at"] = "Today {0}",
        ["yesterday"] = "Yesterday",
        ["restart_title"] = "Restart SharpClip",
        ["restart_body"] = "Restart SharpClip to use this language.",
        ["restart_now"] = "Restart now",
        ["restart_later"] = "Later",
        ["show_welcome"] = "Show welcome again",
        ["queue_title"] = "Clip Queue",
        ["queue_empty"] = "Copy something and it will wait here.",
        ["queue_newest"] = "Paste newest first",
        ["queue_oldest"] = "Paste oldest first",
        ["queue_start"] = "Start Clip Queue",
        ["queue_end"] = "End Clip Queue",
        ["tray_empty"] = "No clipboard history",
        ["check_updates"] = "Check for Updates",
        ["up_to_date"] = "SharpClip {0} is already the latest version.",
        ["feedback"] = "Send Feedback",
        ["open_link"] = "Open Link",
        ["move_board"] = "Move to Pinboard",
        ["change_color"] = "Change Color",
        ["toast_pasted"] = "Pasted",
        ["paste_failed_title"] = "Copied only",
        ["paste_failed_body"] = "Windows did not bring the previous app forward, so Ctrl+V was not sent. The clip is on the clipboard.",
        ["add_foreground"] = "Add the frontmost app",
        ["no_foreground"] = "There is no frontmost app to exclude.",
        ["stats"] = "{0} characters, {1} words, {2} lines",
        ["import_summary"] = "Imported {0} clips and {1} pinboards, skipped {2} duplicates.",
        ["welcome_1_title"] = "Everything you copy, kept",
        ["welcome_1_body"] = "History shows up as cards. Pinboards keep the clips you reuse.",
        ["welcome_2_title"] = "One shortcut away",
        ["welcome_2_body"] = "Press this anywhere to open clipboard history.",
        ["welcome_3_title"] = "You're all set",
        ["welcome_3_body"] = "Copy something, press the shortcut, and it will be there.",
        ["welcome_continue"] = "Continue",
        ["welcome_back"] = "Back",
        ["welcome_start"] = "Start using SharpClip",
        ["welcome_import"] = "Import JSON",
        ["welcome_history"] = "Clipboard history",
        ["welcome_boards"] = "Pinboards",
        ["welcome_private"] = "Free, on this PC",
        ["welcome_private_body"] = "No account and no telemetry.",
    };

    private static readonly Dictionary<string, string> Korean = new()
    {
        ["search"] = "클립보드 검색...",
        ["type"] = "유형",
        ["type_n"] = "유형 ({0})",
        ["date"] = "날짜",
        ["all"] = "전체",
        ["today"] = "오늘",
        ["week"] = "최근 7일",
        ["month"] = "최근 30일",
        ["clear_filters"] = "필터 지우기",
        ["clear_type"] = "유형 필터 지우기",
        ["new_board"] = "새 핀보드",
        ["clear_history"] = "클립보드 기록을 지울까요?",
        ["clear_history_body"] = "핀보드에 없는 클립 {0}개를 기록에서 삭제합니다. 핀보드 항목은 유지됩니다.",
        ["clear"] = "기록 지우기",
        ["paste_fmt"] = "서식과 함께 붙여넣기",
        ["paste_plain"] = "일반 텍스트로 붙여넣기",
        ["paste_one"] = "붙여넣기",
        ["rename"] = "이름 바꾸기",
        ["edit"] = "텍스트 편집",
        ["add_board"] = "핀보드에 추가",
        ["delete_clip"] = "클립 삭제",
        ["remove_board"] = "핀보드에서 제거",
        ["delete_board"] = "핀보드 삭제",
        ["delete_board_body"] = "핀보드만 제거합니다. 클립은 기록에 남습니다.",
        ["rename_board"] = "핀보드 이름 바꾸기",
        ["create"] = "만들기",
        ["cancel"] = "취소",
        ["save"] = "저장",
        ["name"] = "이름",
        ["chars"] = "{0}자",
        ["chars_k"] = "{0}K자",
        ["url_badge"] = "URL",
        ["file_badge"] = "파일",
        ["empty_title"] = "무엇이든 복사하세요",
        ["empty_body"] = "클립보드 기록이 여기에 나타납니다",
        ["no_results"] = "결과 없음",
        ["no_results_body"] = "다른 검색어나 필터를 시도하세요",
        ["text"] = "텍스트",
        ["rich"] = "서식 텍스트",
        ["html"] = "HTML",
        ["image"] = "이미지",
        ["link"] = "링크",
        ["file"] = "파일",
        ["color"] = "색상",
        ["other"] = "기타",
        ["now"] = "지금",
        ["secs"] = "{0}초 전",
        ["mins"] = "{0}분 전",
        ["hours"] = "{0}시간 전",
        ["days"] = "{0}일 전",
        ["weeks"] = "{0}주 전",
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
        ["ok"] = "확인",
        ["general"] = "일반",
        ["appearance"] = "모양",
        ["shortcuts"] = "단축키",
        ["exclusions"] = "제외",
        ["about"] = "정보",
        ["pasting"] = "붙여넣기",
        ["paste_help"] = "클립을 클릭하거나 Enter를 누르면 입력하던 곳에 붙여넣습니다. 끄면 클립만 복사됩니다.",
        ["paste_info"] = "패널이 닫힌 뒤 SharpClip이 Ctrl+V를 누릅니다. Windows가 이전 앱을 앞으로 가져오지 못하면 클립은 복사만 됩니다.",
        ["plain_help"] = "붙여넣는 동안 Shift를 누르면 그 한 번만 반대로 동작합니다.",
        ["plain_info"] = "서식 있는 텍스트와 HTML에서 글꼴, 색, 링크를 빼고 텍스트만 붙여넣습니다.",
        ["tray"] = "알림 영역에 아이콘 표시",
        ["tray_hidden"] = "설정을 다시 열려면 SharpClip을 실행하거나 패널에서 Ctrl+, 를 누르세요.",
        ["tray_hidden_title"] = "트레이 아이콘이 숨겨짐",
        ["tray_hidden_body"] = "SharpClip은 계속 실행됩니다. 설정을 다시 열려면 SharpClip을 실행하거나 패널에서 Ctrl+, 를 누르세요.",
        ["backup"] = "백업",
        ["export_help"] = "기록과 핀보드를 JSON 파일로 내보냅니다.",
        ["import_help"] = "백업 파일을 가져옵니다. 기존 클립은 유지되고 중복은 건너뜁니다.",
        ["export_ok"] = "백업을 내보냈습니다.",
        ["export_fail"] = "내보내기 실패: {0}",
        ["import_ok"] = "클립 {0}개를 가져왔습니다. 중복 {1}개는 건너뛰었습니다.",
        ["import_fail"] = "가져오기 실패: {0}",
        ["theme"] = "테마",
        ["theme_system"] = "시스템",
        ["theme_light"] = "밝게",
        ["theme_dark"] = "어둡게",
        ["animate"] = "패널 애니메이션",
        ["animate_help"] = "기록 패널이 슬라이드되며 열리고 닫힙니다. 끄면 즉시 나타나고 사라집니다.",
        ["animate_reduced"] = "Windows 설정에서 애니메이션 효과가 꺼져 있으면 동작하지 않습니다.",
        ["shortcut_panel"] = "기록 패널 열기/닫기",
        ["shortcut_panel_info"] = "Ctrl, Alt 또는 Windows 키를 다른 키와 함께 누르세요. 예: Alt+V, Ctrl+Shift+P. 기능 키(F1–F12)는 단독으로 쓸 수 있습니다. Shift만으로는 지정할 수 없습니다.",
        ["shortcut_queue"] = "클립 대기열 시작/종료",
        ["shortcut_queue_info"] = "대기열이 켜져 있으면 복사한 내용이 순서대로 쌓이고, Ctrl+V마다 다음 항목을 붙여넣습니다.",
        ["shortcut_queue_help"] = "트레이 메뉴에서도 사용할 수 있습니다.",
        ["shortcut_clear"] = "모든 기록 지우기",
        ["shortcut_quick"] = "빠른 미리보기",
        ["shortcut_quick_info"] = "수정 키 없는 단일 키입니다. 패널이 열려 있을 때만 동작하므로 다른 앱과 충돌하지 않습니다.",
        ["shortcut_quick_help"] = "패널 안에서 카드가 선택된 상태로 누릅니다.",
        ["tab_nav"] = "탭 이동",
        ["tab_history"] = "기록",
        ["tab_boards"] = "앞쪽 핀보드 8개",
        ["tab_nav_help"] = "탭 순서대로 고정된 단축키입니다. 검색 중을 포함해 기록 패널이 열려 있을 때만 동작합니다.",
        ["press_shortcut"] = "단축키를 누르세요…",
        ["press_key"] = "키를 누르세요…",
        ["not_set"] = "설정 안 함",
        ["shortcut_invalid"] = "Ctrl, Alt, Windows 키 또는 기능 키를 사용하세요.",
        ["shortcut_taken"] = "이미 사용 중인 단축키입니다.",
        ["shortcut_reserved"] = "패널 이동에 예약된 키입니다.",
        ["shortcut_failed"] = "Windows가 단축키를 등록하지 못했습니다. 다른 앱이 이미 사용 중일 수 있습니다.",
        ["key_space"] = "Space",
        ["exclude_help"] = "제외한 앱의 클립보드 내용은 저장되지 않습니다.",
        ["about_version"] = "버전 {0}",
        ["about_body"] = "무료이며 계정과 원격 수집이 없습니다.",
        ["lang_system"] = "시스템",
        ["today_at"] = "오늘 {0}",
        ["yesterday"] = "어제",
        ["restart_title"] = "SharpClip 다시 시작",
        ["restart_body"] = "이 언어를 쓰려면 SharpClip을 다시 시작하세요.",
        ["restart_now"] = "지금 다시 시작",
        ["restart_later"] = "나중에",
        ["show_welcome"] = "환영 화면 다시 보기",
        ["queue_title"] = "클립 대기열",
        ["queue_empty"] = "복사한 내용이 여기에 쌓입니다.",
        ["queue_newest"] = "최신 항목부터 붙여넣기",
        ["queue_oldest"] = "오래된 항목부터 붙여넣기",
        ["queue_start"] = "클립 대기열 시작",
        ["queue_end"] = "클립 대기열 종료",
        ["tray_empty"] = "클립 기록이 없습니다",
        ["check_updates"] = "업데이트 확인",
        ["up_to_date"] = "SharpClip {0}은 이미 최신 버전입니다.",
        ["feedback"] = "피드백 보내기",
        ["open_link"] = "링크 열기",
        ["move_board"] = "핀보드로 이동",
        ["change_color"] = "색 변경",
        ["toast_pasted"] = "붙여넣음",
        ["paste_failed_title"] = "복사만 됨",
        ["paste_failed_body"] = "Windows가 이전 앱을 앞으로 가져오지 못해 Ctrl+V를 보내지 않았습니다. 클립은 클립보드에 있습니다.",
        ["add_foreground"] = "맨 앞 앱 추가",
        ["no_foreground"] = "제외할 맨 앞 앱이 없습니다.",
        ["stats"] = "{0}자, {1}단어, {2}줄",
        ["import_summary"] = "클립 {0}개와 핀보드 {1}개를 가져왔고, 중복 {2}개는 건너뛰었습니다.",
        ["welcome_1_title"] = "복사한 내용을 보관합니다",
        ["welcome_1_body"] = "기록은 카드로 표시됩니다. 핀보드는 자주 쓰는 클립을 남겨 둡니다.",
        ["welcome_2_title"] = "단축키 하나면 됩니다",
        ["welcome_2_body"] = "어느 앱에서든 이 키를 눌러 클립보드 기록을 엽니다.",
        ["welcome_3_title"] = "준비되었습니다",
        ["welcome_3_body"] = "무엇인가 복사한 뒤 단축키를 누르면 여기에 있습니다.",
        ["welcome_continue"] = "계속",
        ["welcome_back"] = "뒤로",
        ["welcome_start"] = "SharpClip 시작",
        ["welcome_import"] = "JSON 가져오기",
        ["welcome_history"] = "클립보드 기록",
        ["welcome_boards"] = "핀보드",
        ["welcome_private"] = "이 PC에서 무료",
        ["welcome_private_body"] = "계정과 원격 수집이 없습니다.",
    };

    private static readonly Dictionary<string, string> Chinese = new()
    {
        ["search"] = "搜索剪贴板...",
        ["type"] = "类型",
        ["type_n"] = "类型 ({0})",
        ["date"] = "日期",
        ["all"] = "全部",
        ["today"] = "今天",
        ["week"] = "近 7 天",
        ["month"] = "近 30 天",
        ["clear_filters"] = "清除筛选",
        ["clear_type"] = "清除类型筛选",
        ["new_board"] = "新建 Pinboard",
        ["clear_history"] = "清除剪贴板历史？",
        ["clear_history_body"] = "将删除历史中 {0} 条未加入 Pinboard 的内容。Pinboard 里的条目会保留。",
        ["clear"] = "清除历史",
        ["paste_fmt"] = "带格式粘贴",
        ["paste_plain"] = "按纯文本粘贴",
        ["paste_one"] = "粘贴",
        ["rename"] = "重命名",
        ["edit"] = "编辑文本",
        ["add_board"] = "加入 Pinboard",
        ["delete_clip"] = "删除剪贴",
        ["remove_board"] = "从 Pinboard 移除",
        ["delete_board"] = "删除 Pinboard",
        ["delete_board_body"] = "只删除这个 Pinboard。剪贴内容仍留在历史里。",
        ["rename_board"] = "重命名 Pinboard",
        ["create"] = "创建",
        ["cancel"] = "取消",
        ["save"] = "保存",
        ["name"] = "名称",
        ["chars"] = "{0} 个字符",
        ["chars_k"] = "{0}K 个字符",
        ["url_badge"] = "URL",
        ["file_badge"] = "文件",
        ["empty_title"] = "复制任何内容",
        ["empty_body"] = "剪贴板历史会显示在这里",
        ["no_results"] = "没有结果",
        ["no_results_body"] = "换个搜索词或筛选条件",
        ["text"] = "文本",
        ["rich"] = "富文本",
        ["html"] = "HTML",
        ["image"] = "图片",
        ["link"] = "链接",
        ["file"] = "文件",
        ["color"] = "颜色",
        ["other"] = "其他",
        ["now"] = "刚刚",
        ["secs"] = "{0} 秒前",
        ["mins"] = "{0} 分钟前",
        ["hours"] = "{0} 小时前",
        ["days"] = "{0} 天前",
        ["weeks"] = "{0} 周前",
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
        ["ok"] = "好",
        ["general"] = "通用",
        ["appearance"] = "外观",
        ["shortcuts"] = "快捷键",
        ["exclusions"] = "排除",
        ["about"] = "关于",
        ["pasting"] = "粘贴",
        ["paste_help"] = "点击剪贴或按 Enter，会粘贴到你正在输入的位置。关闭后只复制，不粘贴。",
        ["paste_info"] = "面板关闭后，SharpClip 会替你按下 Ctrl+V。如果系统拒绝把上一个应用带到前台，内容仍会留在剪贴板。",
        ["plain_help"] = "粘贴时按住 Shift，只对这一次反过来。",
        ["plain_info"] = "去掉富文本和 HTML 里的字体、颜色和链接，只粘贴文字。",
        ["tray"] = "在通知区域显示图标",
        ["tray_hidden"] = "要再次打开设置，请重新打开 SharpClip，或在面板中按 Ctrl+,。",
        ["tray_hidden_title"] = "已隐藏托盘图标",
        ["tray_hidden_body"] = "SharpClip 仍在运行。要再次打开设置，请重新打开 SharpClip，或在面板中按 Ctrl+,。",
        ["backup"] = "备份",
        ["export_help"] = "把历史和 Pinboard 导出为 JSON 文件。",
        ["import_help"] = "导入备份。已有剪贴会保留，重复的会跳过。",
        ["export_ok"] = "备份已导出。",
        ["export_fail"] = "导出失败：{0}",
        ["import_ok"] = "已导入 {0} 条剪贴，跳过 {1} 条重复。",
        ["import_fail"] = "导入失败：{0}",
        ["theme"] = "主题",
        ["theme_system"] = "跟随系统",
        ["theme_light"] = "浅色",
        ["theme_dark"] = "深色",
        ["animate"] = "面板动画",
        ["animate_help"] = "历史面板滑入和滑出。关闭后会立刻出现和关闭。",
        ["animate_reduced"] = "Windows 设置里关闭了动画效果时不会播放。",
        ["shortcut_panel"] = "打开或关闭历史面板",
        ["shortcut_panel_info"] = "用 Ctrl、Alt 或 Windows 键再加一个键，例如 Alt+V 或 Ctrl+Shift+P。功能键（F1–F12）可以单独使用。不能只使用 Shift。",
        ["shortcut_queue"] = "开始或结束剪贴队列",
        ["shortcut_queue_info"] = "队列开启时，复制的内容会按顺序加入，每次 Ctrl+V 粘贴下一条。",
        ["shortcut_queue_help"] = "也可以从托盘菜单使用。",
        ["shortcut_clear"] = "清除全部历史",
        ["shortcut_quick"] = "快速预览",
        ["shortcut_quick_info"] = "一个不带修饰键的按键。只在面板打开时生效，因此不会和其他应用冲突。",
        ["shortcut_quick_help"] = "在面板内选中卡片后按下。",
        ["tab_nav"] = "标签切换",
        ["tab_history"] = "历史",
        ["tab_boards"] = "前 8 个 Pinboard",
        ["tab_nav_help"] = "按标签顺序固定的快捷键。只在历史面板打开时生效，搜索时也一样。",
        ["press_shortcut"] = "请按快捷键…",
        ["press_key"] = "请按一个键…",
        ["not_set"] = "未设置",
        ["shortcut_invalid"] = "请使用 Ctrl、Alt、Windows 键，或功能键。",
        ["shortcut_taken"] = "这个快捷键已经在用。",
        ["shortcut_reserved"] = "这个键已留给面板导航。",
        ["shortcut_failed"] = "Windows 未能注册其中一个快捷键。可能已被其他应用占用。",
        ["key_space"] = "空格",
        ["exclude_help"] = "被排除的应用不会保存剪贴板内容。",
        ["about_version"] = "版本 {0}",
        ["about_body"] = "免费，无账号、无遥测。",
        ["lang_system"] = "跟随系统",
        ["today_at"] = "今天 {0}",
        ["yesterday"] = "昨天",
        ["restart_title"] = "重新启动 SharpClip",
        ["restart_body"] = "重新启动后，整个界面会使用这个语言。",
        ["restart_now"] = "立即重新启动",
        ["restart_later"] = "稍后",
        ["show_welcome"] = "重新显示欢迎",
        ["queue_title"] = "剪贴队列",
        ["queue_empty"] = "复制的内容会在这里排队。",
        ["queue_newest"] = "先贴最新",
        ["queue_oldest"] = "先贴最旧",
        ["queue_start"] = "开始剪贴队列",
        ["queue_end"] = "结束剪贴队列",
        ["tray_empty"] = "没有剪贴历史",
        ["check_updates"] = "检查更新",
        ["up_to_date"] = "SharpClip {0} 已是最新版本。",
        ["feedback"] = "发送反馈",
        ["open_link"] = "打开链接",
        ["move_board"] = "移动到 Pinboard",
        ["change_color"] = "改颜色",
        ["toast_pasted"] = "已粘贴",
        ["paste_failed_title"] = "只复制了",
        ["paste_failed_body"] = "系统没有把上一个应用带到前台，因此没有发送 Ctrl+V。内容已经在剪贴板上。",
        ["add_foreground"] = "添加当前前台应用",
        ["no_foreground"] = "没有可以排除的前台应用。",
        ["stats"] = "{0} 个字符，{1} 个词，{2} 行",
        ["import_summary"] = "导入了 {0} 条剪贴和 {1} 个 Pinboard，跳过了 {2} 条重复。",
        ["welcome_1_title"] = "复制过的内容会留下来",
        ["welcome_1_body"] = "历史以卡片显示。Pinboard 留下你反复要用的剪贴。",
        ["welcome_2_title"] = "一个快捷键",
        ["welcome_2_body"] = "在任何应用里按下它，打开剪贴历史。",
        ["welcome_3_title"] = "可以开始了",
        ["welcome_3_body"] = "复制一段内容，按下快捷键，它就会在这里。",
        ["welcome_continue"] = "继续",
        ["welcome_back"] = "返回",
        ["welcome_start"] = "开始使用 SharpClip",
        ["welcome_import"] = "导入 JSON",
        ["welcome_history"] = "剪贴历史",
        ["welcome_boards"] = "Pinboard",
        ["welcome_private"] = "免费，留在这台电脑",
        ["welcome_private_body"] = "无账号、无遥测。",
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

internal static class HotkeyText
{
    public static string Format(bool ctrl, bool shift, bool alt, bool win, int vk)
    {
        if (vk == 0) return Strings.Get("not_set");
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(Name(vk));
        return string.Join("+", parts);
    }

    public static string Name(int vk) => TryName(vk) ?? ("Key " + vk);

    public static bool IsQuickLookKey(int vk) => !Reserved(vk) && TryName(vk) is not null;

    private static bool Reserved(int vk) => vk is 0x1B or 0x0D or 0x09 or 0x2E or (>= 0x25 and <= 0x28);

    private static string? TryName(int vk) => vk switch
    {
        0x20 => Strings.Get("key_space"),
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x2E => "Delete",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => null,
    };
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

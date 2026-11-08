using System.Globalization;

namespace MDEditor;

/// <summary>
/// UI strings (HANDOFF Appendix B). A code table instead of .resw: an unpackaged app otherwise depends on
/// resources.pri language selection, and every string can then be checked in one place.
/// Any zh-* display language uses Simplified Chinese.
/// </summary>
internal static class L
{
    private static readonly bool Chinese =
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Zh = new()
    {
        ["File"] = "文件", ["Edit"] = "编辑", ["Format"] = "格式", ["View"] = "视图", ["Help"] = "帮助",
        ["New"] = "新建", ["Open…"] = "打开…", ["Save"] = "保存", ["Save As…"] = "另存为…",
        ["Export as PDF…"] = "导出为 PDF…", ["Print…"] = "打印…", ["Close"] = "关闭", ["Exit"] = "退出",
        ["Auto Save"] = "自动保存",
        ["Undo"] = "撤销", ["Redo"] = "重做", ["Cut"] = "剪切", ["Copy"] = "复制", ["Paste"] = "粘贴", ["Select All"] = "全选",
        ["Find…"] = "查找…", ["Replace…"] = "替换…", ["Find Next"] = "查找下一个", ["Find Previous"] = "查找上一个",
        ["Find"] = "查找", ["Replace"] = "替换", ["Replace All"] = "全部替换",
        ["Match case"] = "区分大小写", ["Whole word"] = "全字匹配",
        ["Previous"] = "上一个", ["Next"] = "下一个", ["No results"] = "无结果", ["{0} of {1}"] = "第 {0} 个，共 {1} 个",
        ["Bold"] = "粗体", ["Italic"] = "斜体", ["Strikethrough"] = "删除线", ["Inline Code"] = "行内代码", ["Link"] = "链接",
        ["Heading 1"] = "一级标题", ["Heading 2"] = "二级标题", ["Heading 3"] = "三级标题",
        ["Quote"] = "引用", ["Bulleted List"] = "无序列表", ["Task List"] = "任务列表", ["Code Block"] = "代码块",
        ["Editor Only"] = "仅编辑器", ["Split View"] = "分栏视图", ["Preview Only"] = "仅预览",
        ["Switch between editor, split view and preview"] = "在编辑器、分栏和预览之间切换",
        ["Zoom In"] = "放大", ["Zoom Out"] = "缩小", ["Actual Size"] = "实际大小",
        ["About MDEditor"] = "关于 MDEditor", ["Untitled"] = "未命名", ["Version {0}"] = "版本 {0}",
        ["Released under the MIT License."] = "基于 MIT 许可证发布。",
        ["Third-party notices are in THIRD_PARTY_NOTICES.md next to the application."] = "第三方声明见应用目录中的 THIRD_PARTY_NOTICES.md。",
        ["Source code on GitHub"] = "GitHub 上的源代码",
        ["OK"] = "确定", ["Cancel"] = "取消", ["Don't Save"] = "不保存",
        ["Do you want to save changes to {0}?"] = "是否保存对“{0}”的更改？",
        ["The file has changed on disk."] = "文件已在磁盘上被修改。",
        ["{0} was changed by another program. Reload it and lose your edits, or keep your version?"] = "“{0}”已被其他程序修改。要重新加载并丢弃你的修改，还是保留你的版本？",
        ["Reload"] = "重新加载", ["Keep my version"] = "保留我的版本",
        ["The preview component (WebView2) is missing."] = "缺少预览组件（WebView2）。",
        ["Install the WebView2 Runtime to see the preview. Editing still works."] = "请安装 WebView2 运行库以使用预览。编辑功能不受影响。",
        ["Download WebView2 Runtime"] = "下载 WebView2 运行库",
        ["Could not open the file"] = "无法打开文件", ["Could not save the file"] = "无法保存文件",
        ["The file is larger than 50 MB."] = "文件大于 50 MB。",
        ["This looks like a binary file, not text."] = "这似乎是二进制文件，而不是文本。",
        ["Your text is still in this window. Try Save As to choose another location."] = "你的文本仍保留在此窗口中。可以用“另存为”选择其他位置。",
        ["Could not export the PDF"] = "无法导出 PDF",
        ["Preview resources are missing."] = "预览资源缺失。",
        ["Markdown"] = "Markdown", ["Text"] = "文本", ["All files"] = "所有文件", ["PDF"] = "PDF",
        ["Editor"] = "编辑器", ["Preview"] = "预览", ["Divider"] = "分隔条",
        ["{0} words"] = "{0} 字", ["{0} characters"] = "{0} 个字符", ["{0} min read"] = "约 {0} 分钟阅读",
    };

    public static string T(string english) =>
        Chinese && Zh.TryGetValue(english, out string? translated) ? translated : english;

    public static string F(string english, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);
}

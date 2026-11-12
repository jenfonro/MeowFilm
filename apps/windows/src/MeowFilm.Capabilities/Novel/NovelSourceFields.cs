namespace MeowFilm.Capabilities;

internal enum NovelSourceFieldKind { Text, Number, Bool, Json }
internal sealed record NovelSourceField(string Path, string Label, int Rows = 1, NovelSourceFieldKind Kind = NovelSourceFieldKind.Text, string[]? Components = null)
{
    internal string[] Parts => Components ?? Path.Split('.');
    internal string Key => string.Concat(Parts.Select(part => part.Length + ":" + part));
}
internal sealed record NovelSourceTab(string Title, NovelSourceField[] Fields);
internal static class NovelSourceFields
{
    internal static string Title(string protocol) => protocol switch { "legado" => "阅读源", "yiciyuan" => "异次元", "qysg" => "轻悦时光", _ => protocol };
    internal static readonly NovelSourceTab[] Legado = [
        new("基本", [
            new("bookSourceUrl", "源 URL（sourceUrl）", 1, NovelSourceFieldKind.Text),
            new("bookSourceName", "源名称（sourceName）", 1, NovelSourceFieldKind.Text),
            new("bookSourceGroup", "源分组（sourceGroup）", 1, NovelSourceFieldKind.Text),
            new("bookSourceComment", "源注释（sourceComment）", 4, NovelSourceFieldKind.Text),
            new("loginUrl", "登录 URL(loginUrl)", 1, NovelSourceFieldKind.Text),
            new("loginUi", "登录 UI（loginUi）", 1, NovelSourceFieldKind.Text),
            new("loginCheckJs", "登录检查 JS（loginCheckJs）", 1, NovelSourceFieldKind.Text),
            new("coverDecodeJs", "封面解密（coverDecodeJs）", 1, NovelSourceFieldKind.Text),
            new("bookUrlPattern", "书籍 URL 正则（bookUrlPattern）", 1, NovelSourceFieldKind.Text),
            new("header", "请求头（header）", 3, NovelSourceFieldKind.Text),
            new("variableComment", "变量说明(variableComment)", 3, NovelSourceFieldKind.Text),
            new("concurrentRate", "并发率（concurrentRate）", 1, NovelSourceFieldKind.Text),
            new("jsLib", "jsLib", 3, NovelSourceFieldKind.Text),
        ]),
        new("搜索", [
            new("searchUrl", "搜索地址（url）", 4, NovelSourceFieldKind.Text),
            new("ruleSearch.checkKeyWord", "校验关键字（checkKeyWord）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.bookList", "书籍列表规则（bookList）", 8, NovelSourceFieldKind.Text),
            new("ruleSearch.name", "书名规则（name）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.author", "作者规则（author）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.kind", "分类规则（kind）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.wordCount", "字数规则（wordCount）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.lastChapter", "最新章节规则（lastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.intro", "简介规则（intro）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.coverUrl", "封面规则（coverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleSearch.bookUrl", "详情页 URL 规则（bookUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("发现", [
            new("exploreUrl", "发现地址规则（url）", 12, NovelSourceFieldKind.Text),
            new("ruleExplore.bookList", "书籍列表规则（bookList）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.name", "书名规则（name）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.author", "作者规则（author）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.kind", "分类规则（kind）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.wordCount", "字数规则（wordCount）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.lastChapter", "最新章节规则（lastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.intro", "简介规则（intro）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.coverUrl", "封面规则（coverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleExplore.bookUrl", "详情页 URL 规则（bookUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("详情", [
            new("ruleBookInfo.init", "预处理规则（bookInfoInit）", 3, NovelSourceFieldKind.Text),
            new("ruleBookInfo.name", "书名规则（name）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.author", "作者规则（author）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.kind", "分类规则（kind）", 3, NovelSourceFieldKind.Text),
            new("ruleBookInfo.wordCount", "字数规则（wordCount）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.lastChapter", "最新章节规则（lastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.intro", "简介规则（intro）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.coverUrl", "封面规则（coverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.tocUrl", "目录 URL 规则（tocUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.canReName", "允许修改书名作者（canReName）", 1, NovelSourceFieldKind.Text),
            new("ruleBookInfo.downloadUrls", "下载URL规则(downloadUrls)", 1, NovelSourceFieldKind.Text),
        ]),
        new("目录", [
            new("ruleToc.preUpdateJs", "更新之前 JS（preUpdateJs）", 3, NovelSourceFieldKind.Text),
            new("ruleToc.chapterList", "目录列表规则（chapterList）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.chapterName", "章节名称规则（ChapterName）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.chapterUrl", "章节 URL 规则（chapterUrl）", 4, NovelSourceFieldKind.Text),
            new("ruleToc.formatJs", "格式化规则(formatJs)", 3, NovelSourceFieldKind.Text),
            new("ruleToc.isVolume", "Volume 标识（isVolume）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.updateTime", "更新时间（ChapterInfo）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.isVip", "VIP 标识（isVip）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.isPay", "购买标识（isPay）", 1, NovelSourceFieldKind.Text),
            new("ruleToc.nextTocUrl", "目录下一页规则（nextTocUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("正文", [
            new("ruleContent.content", "正文规则（content）", 1, NovelSourceFieldKind.Text),
            new("ruleContent.title", "章节名称规则（ChapterName）", 1, NovelSourceFieldKind.Text),
            new("ruleContent.nextContentUrl", "正文下一页 URL 规则（nextContentUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleContent.webJs", "WebView JS（webJs）", 3, NovelSourceFieldKind.Text),
            new("ruleContent.sourceRegex", "资源正则（sourceRegex）", 3, NovelSourceFieldKind.Text),
            new("ruleContent.replaceRegex", "替换规则（replaceRegex）", 3, NovelSourceFieldKind.Text),
            new("ruleContent.imageStyle", "图片样式（imageStyle）", 3, NovelSourceFieldKind.Text),
            new("ruleContent.imageDecode", "图片解密（imageDecode）", 3, NovelSourceFieldKind.Text),
            new("ruleContent.payAction", "购买操作（payAction）", 3, NovelSourceFieldKind.Text),
        ]),
    ];
    internal static readonly NovelSourceTab[] Yiciyuan = [
        new("基本", [
            new("bookSourceUrl", "源 URL（bookSourceUrl）", 1, NovelSourceFieldKind.Text),
            new("bookSourceName", "源名称（bookSourceName）", 1, NovelSourceFieldKind.Text),
            new("bookSourceGroup", "源分组（bookSourceGroup）", 1, NovelSourceFieldKind.Text),
            new("bookSourceComment", "源注释（bookSourceComment）", 4, NovelSourceFieldKind.Text),
            new("loginUrl", "登录 URL（loginUrl）", 1, NovelSourceFieldKind.Text),
            new("loginUi", "登录 UI（loginUi）", 3, NovelSourceFieldKind.Text),
            new("loginCheckJs", "登录检查 JS（loginCheckJs）", 3, NovelSourceFieldKind.Text),
            new("coverDecodeJs", "封面解密（coverDecodeJs）", 3, NovelSourceFieldKind.Text),
            new("ruleBookUrlPattern", "书籍 URL 正则（ruleBookUrlPattern）", 1, NovelSourceFieldKind.Text),
            new("httpUserAgent", "User-Agent（httpUserAgent）", 1, NovelSourceFieldKind.Text),
            new("variableComment", "变量说明（variableComment）", 3, NovelSourceFieldKind.Text),
            new("jsLib", "jsLib", 3, NovelSourceFieldKind.Text),
            new("phonehttp", "使用移动网络（phonehttp）", 1, NovelSourceFieldKind.Bool),
            new("serialNumber", "排序（serialNumber）", 1, NovelSourceFieldKind.Number),
        ]),
        new("搜索", [
            new("ruleSearchUrl", "搜索地址（ruleSearchUrl）", 4, NovelSourceFieldKind.Text),
            new("checkKeyWord", "校验关键字（checkKeyWord）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchList", "书籍列表规则（ruleSearchList）", 8, NovelSourceFieldKind.Text),
            new("ruleSearchName", "书名规则（ruleSearchName）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchAuthor", "作者规则（ruleSearchAuthor）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchKind", "分类规则（ruleSearchKind）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchLastChapter", "最新章节规则（ruleSearchLastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchIntroduce", "简介规则（ruleSearchIntroduce）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchCoverUrl", "封面规则（ruleSearchCoverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleSearchNoteUrl", "详情页 URL 规则（ruleSearchNoteUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("发现", [
            new("ruleFindUrl", "发现地址规则（ruleFindUrl）", 12, NovelSourceFieldKind.Text),
            new("ruleFindList", "书籍列表规则（ruleFindList）", 1, NovelSourceFieldKind.Text),
            new("ruleFindName", "书名规则（ruleFindName）", 1, NovelSourceFieldKind.Text),
            new("ruleFindAuthor", "作者规则（ruleFindAuthor）", 1, NovelSourceFieldKind.Text),
            new("ruleFindKind", "分类规则（ruleFindKind）", 1, NovelSourceFieldKind.Text),
            new("ruleFindLastChapter", "最新章节规则（ruleFindLastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleFindIntroduce", "简介规则（ruleFindIntroduce）", 1, NovelSourceFieldKind.Text),
            new("ruleFindCoverUrl", "封面规则（ruleFindCoverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleFindNoteUrl", "详情页 URL 规则（ruleFindNoteUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("详情", [
            new("ruleBookInfoInit", "预处理规则（ruleBookInfoInit）", 3, NovelSourceFieldKind.Text),
            new("ruleBookName", "书名规则（ruleBookName）", 1, NovelSourceFieldKind.Text),
            new("ruleBookAuthor", "作者规则（ruleBookAuthor）", 1, NovelSourceFieldKind.Text),
            new("ruleBookKind", "分类规则（ruleBookKind）", 3, NovelSourceFieldKind.Text),
            new("ruleBookLastChapter", "最新章节规则（ruleBookLastChapter）", 1, NovelSourceFieldKind.Text),
            new("ruleIntroduce", "简介规则（ruleIntroduce）", 1, NovelSourceFieldKind.Text),
            new("ruleCoverUrl", "封面规则（ruleCoverUrl）", 1, NovelSourceFieldKind.Text),
            new("ruleChapterUrl", "目录 URL 规则（ruleChapterUrl）", 1, NovelSourceFieldKind.Text),
        ]),
        new("目录", [
            new("ruleChapterList", "目录列表规则（ruleChapterList）", 1, NovelSourceFieldKind.Text),
            new("ruleChapterName", "章节名称规则（ruleChapterName）", 1, NovelSourceFieldKind.Text),
            new("ruleContentUrl", "章节 URL 规则（ruleContentUrl）", 4, NovelSourceFieldKind.Text),
            new("ruleChapterUrlNext", "目录下一页规则（ruleChapterUrlNext）", 1, NovelSourceFieldKind.Text),
            new("ruleChapterReverse", "倒序目录（ruleChapterReverse）", 1, NovelSourceFieldKind.Bool),
        ]),
        new("正文", [
            new("ruleBookContent", "正文规则（ruleBookContent）", 5, NovelSourceFieldKind.Text),
            new("title", "章节名称规则（title）", 1, NovelSourceFieldKind.Text),
            new("ruleContentUrlNext", "正文下一页 URL 规则（ruleContentUrlNext）", 1, NovelSourceFieldKind.Text),
            new("ruleBookContentReplace", "替换规则（ruleBookContentReplace）", 3, NovelSourceFieldKind.Text),
            new("imageStyle", "图片样式（imageStyle）", 3, NovelSourceFieldKind.Text),
            new("imageDecode", "图片解密（imageDecode）", 3, NovelSourceFieldKind.Text),
            new("ruleBookContentDecoder", "正文图片解密（ruleBookContentDecoder）", 3, NovelSourceFieldKind.Text),
            new("payAction", "购买操作（payAction）", 3, NovelSourceFieldKind.Text),
        ]),
    ];
    internal static readonly NovelSourceTab[] Qysg = [
        new("基本", [
            new("bookSourceUrl", "源 URL（bookSourceUrl）", 1, NovelSourceFieldKind.Text),
            new("bookSourceName", "源名称（bookSourceName）", 1, NovelSourceFieldKind.Text),
            new("bookSourceGroup", "源分组（bookSourceGroup）", 1, NovelSourceFieldKind.Text),
            new("bookSourceComment", "源注释（bookSourceComment）", 4, NovelSourceFieldKind.Text),
            new("customOrder", "排序（customOrder）", 1, NovelSourceFieldKind.Number),
        ]),
        new("定义", [
            new("html", "HTML／脚本或远程候选地址（html）", 20, NovelSourceFieldKind.Text),
        ]),
        new("登录与请求", [
            new("login", "支持登录（login）", 1, NovelSourceFieldKind.Bool),
            new("header", "请求头（header）", 4, NovelSourceFieldKind.Text),
        ]),
    ];
    internal static NovelSourceTab[] Tabs(string protocol) => protocol switch { "legado" => Legado, "yiciyuan" => Yiciyuan, "qysg" => Qysg, _ => throw new System.IO.InvalidDataException("不支持的书源协议") };
}

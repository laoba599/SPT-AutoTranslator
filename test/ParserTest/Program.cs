using AutoTranslator;

// 离线单测：不联网，只验谷歌返回解析和文本切分保护。
var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok) failures++;
}

// 1. gtx 返回：多段译文拼起来。
var parsed = GoogleFreeTranslator.ParseResponse(
    "[[[\"你好世界\",\"Hello world\",null,null,10]],null,\"en\"]");
Check(parsed == "你好世界", "google single segment");

parsed = GoogleFreeTranslator.ParseResponse(
    "[[[\"你好\",\"Hello\",null,null,3],[\"世界\",\"world\",null,null,3]],null,\"en\"]");
Check(parsed == "你好世界", "google multi segment");

// 2. 切分：占位符、标签、链接、中文片段原样保留，其余可翻译。
var segs = TextSegmenter.Split("Bring {0} bottles of <b>water</b> see https://example.com/a 你好，go!");
var literals = segs.Where(s => s.literal).Select(s => s.text).ToList();
var translatables = segs.Where(s => !s.literal).Select(s => s.text).ToList();
Check(literals.Contains("{0}") && literals.Contains("<b>") && literals.Contains("</b>")
    && literals.Contains("https://example.com/a") && literals.Contains("你好，"),
    "segmenter keeps literals");
Check(translatables.Contains("Bring ") && translatables.Contains(" bottles of ")
    && translatables.Contains("water") && translatables.Contains(" see ") && translatables.Contains("go!"),
    "segmenter keeps translatables");

// 3. 跳过规则：空白、纯符号、已是中文不用翻译。
Check(TextSegmenter.NeedsNoTranslation(""), "skip empty");
Check(TextSegmenter.NeedsNoTranslation("123 !!!"), "skip symbols");
Check(TextSegmenter.NeedsNoTranslation("你好世界"), "skip chinese");
Check(!TextSegmenter.NeedsNoTranslation("Hello world"), "do not skip english");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURES");
return failures;

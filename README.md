# SPT 离线翻译工具 v1（AutoTranslator）

在 **SPT 服务器关闭时**运行的离线翻译工具：扫描已安装 mod 的语言文件，
把缺失的中文翻译补齐，改动前自动备份，出问题一键恢复。
**不是**装进 user/mods 的服务器 mod，不用开服、不用改服务器代码。

## 用法

1. 确认 SPT 服务器已关闭。
2. 双击 `AutoTranslator.exe`。
3. 菜单 1 设置 SPT 安装根目录（包含 user 文件夹的那一层，如 `D:\SPT`）。
4. 菜单 2 扫描：列出各 mod 缺中文的语言文件和条数。
5. 菜单 3 翻译：先自动备份，再逐条翻译。
6. 开服进游戏验证。进游戏不正常 → 回到工具，菜单 4 一键恢复。

## 翻译规则

- 只认英文源文件，目标语言默认 `ch`（SPT 简体中文代码）。
- 已有中文的条目跳过；目标文件已有的译文绝不覆盖，只补缺失的 key。
- 源文件（英文）永远不动；翻译写进新文件（如 `ch.json`）或已有目标文件的缺失位置。
- 占位符（`{0}`、`{name}`）、HTML 标签、链接、百分号格式（`%s`）、中文片段原样保留，不送翻译。
- 识别两种语言文件形态：
  - 任务文本类：`Locales` 文件夹里的扁平词典，或 `en.json` / `*english*.json`；
  - 内嵌类：物品、服装等 JSON 里带 `locales: { "en": {...} }` 段的条目。
- 翻译结果有缓存（`cache.json`），重复运行不重复花钱、不重复请求。

## 翻译源（菜单 5 设置，也可直接改 config.json）

- `google_free`：免费谷歌，不用 key。注意这是非官方接口，部分网络下会被谷歌
  风控页拦截（报错里会写明），遇到请稍后重试或改用 DeepL；大批量请求会限流，
  工具默认每条间隔 300 毫秒。
- `deepl`：需 DeepL API Key。免费版 Key 用默认端点 `api-free`；
  Pro 账号请把 `config.json` 里的 `DeeplEndpoint` 改成 `https://api.deepl.com/v2/translate`。

`config.json` 就在 exe 旁边，明文保存 DeepL Key（只存在你自己电脑上，
不要把 config.json 发给别人）。

## 备份与恢复

- 每次翻译运行产生一个快照目录：`backup\yyyyMMdd-HHmmss\`，里面按原相对路径
  存放被覆盖的原文件，并附 `manifest.json` 清单。
- 恢复（菜单 4）默认恢复**最近一次**快照：被覆盖的文件拷回原样，
  翻译新建的文件删掉。快照目录本身会保留，可重复恢复。
- 旧快照会越积越多，可手动删除 `backup` 下不需要的旧快照文件夹。

## 做不到的事（先说清楚）

- 写死在代码里、嵌在 DLL 里的文本扫不到、翻不了（比如某些商人简介就是写死在代码里的）。
- 没有英文源文件的 mod（如只有俄文）不会翻；只有俄文源的以后可再加源语言选项。
- 本工具不判断译文质量；机翻难免有怪句子，进游戏后请抽查，个别条目可手动改
  目标文件（工具下次运行时不会覆盖你手改的译文）。
- 翻译时请保持 SPT 服务器关闭；工具检测到服务器进程会先警告。

## 构建（给想自己改代码的人）

源码在 `src/AutoTranslator`（C#、.NET 9，无第三方依赖）。

```bash
# Linux 下编译测试
dotnet build src/AutoTranslator
# 发布 Windows 单文件版（自带运行库，目标电脑不用装 .NET）
dotnet publish src/AutoTranslator -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

`test/` 里有假 mod 文件夹和解析单测，可在没装 SPT 的机器上验证
扫描、翻译（缓存）、备份、恢复全流程。

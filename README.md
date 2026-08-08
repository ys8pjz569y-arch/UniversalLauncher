# 通用一键启动器（UniversalLauncher）

一个通用、可分享的 Windows 一键启动器：在界面上选择要启动的应用、要打开的网址，切换输入法和声音设备，保存成多套方案后，一键全部执行。

## 功能

- **多套方案**：自定义并命名多套方案，随时切换 —— 一套方案对应一套「游戏 / 工作 / 刷视频」配置
- **启动应用**：最多 8 个应用，支持已安装应用列表选择 + 搜索 + 浏览任意 .exe
- **打开网址**：最多 8 个网址，用系统默认浏览器打开，未写前缀时自动补全 `https://`
- **切换输入法**：如 美式键盘 / 简体中文
- **切换声音设备**：声音输入（麦克风）、声音输出（扬声器）
- **配置持久化**：方案保存在 `profiles.txt`，下次打开自动加载上次激活的方案
- **单文件程序**：编译成单个 `.exe`，无需安装，Win10 / Win11 可直接运行

## 使用

1. 双击 `UniversalLauncher.exe`
2. 填好 输入法 / 声音设备 / 应用 / 网址
3. 点「新增方案」起个名字保存
4. 点「一键启动」

## 编译（需要 .NET Framework 4.x，Win10/11 自带 csc.exe）

```
csc -nologo -target:winexe -platform:anycpu -codepage:65001 -out:UniversalLauncher.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:Microsoft.CSharp.dll UniversalLauncher.cs
```

## 配置格式

配置为纯文本，可手改。`config.txt` 保存当前激活的方案，`profiles.txt` 保存所有方案：

```
[方案名]
input=00000409                              # 键盘布局ID（00000409=美式键盘 / 00000804=简体中文），留空=不切换
capture={0.0.1.00000000}.{...}              # 声音输入设备ID
render={0.0.0.00000000}.{...}               # 声音输出设备ID
apps=C:\a.exe|D:\b.exe                      # 应用路径，多个用 | 分隔
urls=https://www.xxx.com|https://yyy.com    # 网址，多个用 | 分隔
```

> 注意：`config.txt`、`profiles.txt` 包含个人设备 ID 与应用路径，默认被 `.gitignore` 排除，不会提交到仓库。仓库内提供了 `config.sample.txt` / `profiles.sample.txt` 作为格式示例。

# Application artwork

`HM-AppKit.svg` is the editable source for the HM monogram. The PNG and multi-resolution ICO use the same vector geometry; the ICO includes 16, 24, 32, 48, 64, 128, and 256 px entries.

The application logos identify the listed products. They were extracted read-only from existing official applications or the user's official installation packages. No installation package is bundled with these resources and no application is executed to obtain its icon.

| Resource | Source |
| --- | --- |
| feishu.png | Installed Feishu `icon.ico` |
| weixin.png | Installed Weixin `Weixin.exe`, primary icon |
| todesk.png | Installed ToDesk `ToDesk.exe`, primary icon |
| jianying.png | Installed Jianying Pro `JianyingPro.exe`, primary icon |
| qianniu.png | `qianniu_(9.95.01N)_NK_64.exe`, primary branded icon |
| doudian.png | `doudian_v1.1.7_x64.exe`, primary branded icon |
| jingmai.png | `JdWorkStationSetup13.4.6.exe` → `install.7z` → `JMWorkStation.exe`, primary application icon |
| pinduoduo.png | `PddSetup_V3.6.8.3_Build20260629131411_Final.exe` → `icon/PddWorkbench.ico` |
| qianfan.png | `千帆客服工作台_setup_1.2.6.exe`, primary branded icon |
| kuaiyu.png | `kwaixiaodian-Setup-1.0.3.1539.exe`, primary branded icon |
| dewu.png | `得物商家客服3.0.exe` → inner `得物商家客服3.0.exe`, primary application icon |

Package contents were read using the console binaries extracted from the official [7-Zip 26.04 release](https://github.com/ip7z/7zip/releases/tag/26.04). Only explicitly named payloads were extracted, using flat output paths. Windows icon-resource APIs were used to render 256 px PNG resources. Installer icons with packaging-box overlays were replaced by the underlying application icons.

`AppLogoAssets` maps the built-in catalog IDs to WPF resource images. Custom applications retain the existing initial-letter fallback. Resources load from the executable without network access or installed-application dependencies.

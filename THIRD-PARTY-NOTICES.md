# 引用、素材与致谢

本项目是独立的 Windows 桌面挂件，与 OpenAI、Apple、DeepSeek 及下列项目的作者没有官方合作或背书关系。

## 实际使用的第三方素材

### Sutera-Diffusus / dsh-whale-musume

- 项目：https://github.com/Sutera-Diffusus/dsh-whale-musume
- 作者：Sutera-Diffusus
- 固定来源版本：[`edd215dd8cab297084d6a45be6d69f02a800ecfc`](https://github.com/Sutera-Diffusus/dsh-whale-musume/tree/edd215dd8cab297084d6a45be6d69f02a800ecfc)
- 许可：MIT。完整版权声明及许可见 [licenses/dsh-whale-musume-MIT.txt](licenses/dsh-whale-musume-MIT.txt)。
- 使用范围：92 张鲸鱼娘 WebP 立绘无损转换为 `assets/` 内 PNG；其中 89 张为完整姿态，3 张为侧边探头。`whale.ico` 从上述角色素材制作。
- 修改方式：改变文件格式、缩放显示，并在 WPF 中增加呼吸、挤压、摇晃、弹性、粒子和短距离惯性效果。未在运行时加载上游 JavaScript、CSS 或 DeepSeek Harness。
- 本项目只实现 README 中列出的功能，不包含上游的成就、小游戏或跨端存档系统。角色美术不作为本项目原创成果宣称。

## 统计思路与调研参考

以下项目用于理解本机日志统计与状态展示方案。本项目的解析、窗口与物理代码单独编写，没有打包它们的源码或可执行文件。

| 来源 | 参考内容 | 上游声明许可 |
| --- | --- | --- |
| [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar) | Codex 本机日志、TPS、token 与缓存状态展示思路 | MIT；以上游仓库许可为准 |
| [zzccchen/token-pulse](https://github.com/zzccchen/token-pulse) | 本地只读、未知数据保持未知、速度统计展示的调研参考 | MIT；以上游仓库许可为准 |

## 界面设计参考

[Apple Human Interface Guidelines — Materials](https://developer.apple.com/design/human-interface-guidelines/materials)：参考材质层级、克制强调和可读性原则。界面用 WPF 自行实现，没有使用 Apple 的图标、字体文件或品牌素材；不是 Apple 原生 Liquid Glass。

## 胡萝卜皮肤（非官方二创）

形象参考 STAYREAL 卜卜胡萝卜周边：https://hkmall.istayreal.com/products/lk24003 。图集由 AI 辅助生成，包含 12 款独立表情。未使用五月天音乐、商品摄影文件或官方商标。代码的 MIT 许可不授予原角色权利，详见 [皮肤来源说明](assets/carrot/SOURCE.md)。

## 系统组件

使用 Windows 自带的 .NET Framework 4.8、WPF、Windows Forms 托盘及系统字体，不随发行包重新分发这些组件。字体按使用者电脑上已安装的字体显示。

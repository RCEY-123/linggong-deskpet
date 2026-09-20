// ============================================================================
// InstallerInfo.cs —— 安装器 / 卸载器的程序集元数据
// ----------------------------------------------------------------------------
// 作用：让「灵工桌宠-安装程序.exe」与「卸载-灵工桌宠.exe」在
//       Windows 文件属性 / 属性页里就能看到产品名、版本与创作者，
//       而不是一片空白（FileVersion=0.0.0.0、公司为空）。
//
// 说明：
//   · 这个文件同时被 DeskPetSetup.cs 与 Uninstaller.cs 的编译命令行引用，
//     所以两份产物带的是同一套元数据，版本号与各自的 Const.Version 保持一致。
//   · 只加元数据，不引用任何业务类型，也不改动任何原有逻辑。
// ============================================================================
using System.Reflection;
using System.Runtime.InteropServices;

// 安装器与卸载器共用这个文件：卸载器编译时带 /define:UNINSTALLER，
// 于是两个 exe 在文件属性里各自显示成「安装程序 / 卸载程序」。
#if UNINSTALLER
[assembly: AssemblyTitle("灵工桌宠 卸载程序")]
[assembly: AssemblyProduct("灵工桌宠")]
[assembly: AssemblyDescription("灵工桌宠 v1.1.0 卸载程序（只删本产品清单内的文件，不误删、不穿透、不绕过安全校验）")]
#else
[assembly: AssemblyTitle("灵工桌宠 安装程序")]
[assembly: AssemblyProduct("灵工桌宠")]
[assembly: AssemblyDescription("灵工桌宠 v1.1.0 安装程序（内置 13 位角色：信浓 · 初月 · 欧根亲王 · 岛风 · 企业 · 米雪儿 · 艾卡 · 佩丽卡 · 庄方宜 · 梨诺 · 叶瞬光 · 橘福福 · 蕾米埃尔）")]
#endif
[assembly: AssemblyCompany("睡不着のHATSUZUKI")]
[assembly: AssemblyCopyright("Copyright © 睡不着のHATSUZUKI · 灵工巧物 天祈智临 · MIT License")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: ComVisible(false)]

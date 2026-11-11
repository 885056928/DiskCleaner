---
version: alpha
name: "C 盘清理"
description: "以磁盘容量条为视觉锚点的 Windows 清理工具"
colors:
  primary: "#076D60"
  background: "#F0F4F4"
  surface: "#FFFFFF"
  ink: "#142D30"
  muted: "#486466"
  line: "#D5E2E2"
  disk: "#10393A"
  capacity: "#88DDC3"
  capacity-track: "#326061"
  selected: "#E3F3EE"
  danger: "#A83434"
typography:
  sans:
    fontFamily: "Segoe UI, Microsoft YaHei UI"
rounded:
  control: "6px"
  panel: "9px"
  capacity: "11px"
spacing:
  window-inset: "26px"
  panel-inset: "16px"
components:
  button: {}
  tabs: {}
  grid: {}
  dialog: {}
---

# C 盘清理界面

## Overview

简体中文 Windows 桌面工具，面向需要检查 C 盘占用并手动释放空间的用户。磁盘面板在两个标签上方持续显示已用比例和可用容量。配色和共享控件以 `src/DiskCleaner/Theme.xaml` 为准。

## Colors

深青色用于磁盘面板，薄荷色显示已用容量。浅青色标示选中类别，深绿用于扫描和清理入口，红色只用于确认窗口中的永久删除。

## Typography

使用 Segoe UI 和 Microsoft YaHei UI。标题、可用容量和分类名称使用半粗体，文件数据与状态提示保持常规字重。大小使用二进制换算，修改时间使用本地时间。

## Layout

垃圾清理页左侧是类别，右侧是该类别的文件明细。大文件页使用通栏表格。两页共用磁盘概览和底部进度区域，表格在各自面板内滚动。

## Elevation & Depth

使用浅底色、白色工作区和细描边区分层次。永久删除确认由拥有者窗口居中的模态窗口承载。

## Shapes

按钮使用 6 DIP 圆角，面板使用 9 DIP 圆角，容量卡使用 11 DIP 圆角。保留 Windows 原生窗口边框。

程序和窗口使用同一枚磁盘图标，沿用主界面的深青底和薄荷色磁盘轮廓。ICO 包含 16 至 256 像素的尺寸，用于标题栏、任务栏和资源管理器。

## Components

按钮、标签、列表选择和表格样式由主题资源字典共享。下拉框、复选框和滚动条采用 WPF 原生控件，表格列支持排序，长路径可在提示中查看。删除确认默认聚焦取消。扫描可取消，批量删除期间显示处理进度。磁盘图标和容量条提供概览，旁边的文字给出相同信息。

## Do's and Don'ts

- 显示确切的文件数量和大小，并在同一位置报告空结果、权限跳过和扫描完成状态。
- 大文件表格支持原生整行单选和多选，定位适用于单个文件。删除前复用永久删除确认框，删除完成后移除成功项并显示跳过数量。垃圾清理使用勾选类别的扫描快照。

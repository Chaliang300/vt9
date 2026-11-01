# Dock 局部文字透镜

滑块覆盖区域显示 1.20 倍、Bold 的紫蓝文字；未覆盖区域保持原来的 18px Medium 灰色文字。同一个词被部分覆盖时，变化仅发生在覆盖到的笔画上。

WPF 使用与胶囊共享位移、缩放的反向几何裁剪，隐藏覆盖区域里的原字，避免半透明玻璃下出现大小两套字的重影。透镜仍沿用原来的逆变换补偿，字形不会随胶囊拉伸变形。SVG 参考同步采用同形状、同变换的反向遮罩。

保留现有 Acrylic、透明度、页面布局、液态拖拽与统一切页逻辑。

验证结果：程序自检 105 项、原生液态交互 14 项、SVG 液态交互 22 项，以及本次原生局部透镜 7 项、浏览器遮罩 4 项均通过。截图为 WPF 离屏渲染，不包含桌面 DWM 背景。未进行设备写入。

相关输出位于 `artifacts/svg-refine/data`：`dock-lens-half.png`、`dock-lens-full.png`、`dock-local-lens-test.json`、`svg-lens-half.png`、`svg-local-lens-test.json`。

构建产物已更新根目录 `VT9Status.exe`。现有运行实例仍是 dist 目录内旧版，需要从托盘退出旧实例后运行根目录程序查看。

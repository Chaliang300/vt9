# SVG 界面精修验证记录（2026-11-01）

## 范围

按用户确认执行：保留原有 Acrylic、透明度、颜色、描边和阴影策略；布局、字体、控件造型和动效采用新 SVG。

## 已完成

- 概况顶部卡片高度344，启动与提醒卡移至436、146高；修复开关标签与说明间距。
- 顶栏操作区、页脚状态、DPI数值及加减按钮按参考位置调整。
- DPI快捷按钮采用不同宽度及6px间距。参考稿整组会超过卡片右边界，实际左移26px，保留24px卡片内边距；按钮仍显示设备真实档位。
- 标题20/700、卡片标题18/700、电量15/700、DPI大数字40/800；西文使用Segoe UI，中文回退微软雅黑UI。
- Dock文字增加0.5px字距，取消1.08倍缩放；设备图形采用60×100简化轮廓。
- 统一14px圆头滑轨及实心圆点，保留项目原色。填充段延伸至滑块圆心，避免细缝。
- 卡片420ms、错峰55ms；Dock320ms；开关220ms弹性曲线。按CSS cubic-bezier的X坐标求解时间，分别实现三种曲线。
- Dock取消越界阻尼、挤压和速度甩动，保留4px拖动阈值、范围0..312及就近吸附。
- 保留动画中断续接；修复同页重新入场时清动画前未保存当前透明度的问题。
- 普通读数即时更新，取消额外缩放。

## 保留项核对

与 backup/before-svg-refine-20261101-134603/src 的文件哈希比较：Theme.cs、MainForm.Appearance.cs、MainForm.cs、HidReader.cs、DpiController.cs、ConfigBlocks.cs、MainForm.Dpi.cs 全部未变。

## 验证

- build.ps1 -OutputDirectory artifacts/svg-refine：编译通过。
- 自检：66项通过，包括既有HID/参数控制测试与新增贝塞尔曲线端点、时间反解、单调性、开关回弹检查。
- 交互检查：8项通过，包括Dock上下界、最近项吸附、反向切页、连续开关、DPI即时读数、恢复草稿和全部卡片落定。
- 交互脚本只修改内存草稿，没有点击应用、快捷档位或修改启动设置；未发设备配置写命令。
- 四页WPF渲染图已逐张检查（当前系统125%，1275×825）。未执行100%和150%实际系统缩放检查。
- 动画打断诊断最终：设置页可见、pillY=312；其他页Collapsed；全部卡片X=0、Opacity=1，无残留Effect/Cache。

## 环境限制与复现

本会话直接启动新EXE返回“操作已被用户取消”。通过Windows PowerShell STA加载编译程序集执行自检和预览，诊断输出目录设为artifacts/svg-refine/data。

桌面CopyFromScreen返回“句柄无效”，因此预览自动回退为WPF RenderTargetBitmap。PNG可核对排版和控件；原生DWM背景不在图中，不能用这些图判断实际Acrylic合成效果。该会话托盘注册也失败，未将其视为产品托盘回归测试。

SettingsTests的临时快捷方式目录统一改为Program.DataDir，使常规运行和诊断宿主使用同一个可写测试目录。

复现脚本：experiments/verify-svg-refine.ps1、experiments/verify-svg-controls.ps1。诊断数据：artifacts/svg-refine/data/self-test.json、controls-test.json、motion-report-final.json；四页图片为ui-preview-bg-*.png。

根目录VT9Status.exe更新为已编译的同一份程序。原有运行实例来自dist/VT9Status-1.17.0，未终止该实例；请退出旧实例后从根目录打开新版。

## Dock 上下边界对齐补充

Dock现在跟随当前页面卡片组的实际排列边界。125%缩放下，概况/DPI实际边界为76..581.6，参数/设置为76..582.4；逐页实测Dock与卡片上下边界相等，消除了分别取整造成的1物理像素偏差。保留原有材质和颜色。12项交互检查通过，边界记录见artifacts/svg-refine/data/dock-alignment.json。根目录EXE已更新。

## Dock 松开后继续跟随鼠标修复

确认需求为修复松开后仍跟随鼠标。MouseMove检测到左键已释放或捕获丢失时，清理dragDown/isSwiping并释放捕获；鼠标释放改用PreviewMouseLeftButtonUp处理；失去捕获和窗口失焦统一取消手势。取消时仅吸附至正确位置，后续悬停不再移动。

编译通过。专用回归脚本experiments/verify-dock-release.ps1通过5项检查（含已释放鼠标前置条件），验证丢失MouseUp后的状态清理、释放后移动不改变滑块、丢失捕获清理待点击状态、窗口失焦取消手势。未执行设备写入。报告artifacts/svg-refine/data/dock-release-test.json。完整交互套件重跑时原有“连续开关落位”检查出现不稳定，不能将本次描述为完整套件全部通过；本次改动未涉及开关动画。根目录EXE已更新。

## 移除底栏与诊断复制入口

移除底部“收起至托盘”“复制诊断信息”及最后接收时间/充电状态整行；同时移除托盘菜单的诊断复制入口及已无调用的剪贴板诊断方法。窗口与页面高度从660收紧至606，保留卡片底部约24逻辑像素留白。右上角×调用统一HideToTray，不退出后台进程；正常托盘注册时隐藏，注册失败时沿用现有保留窗口的处理。

编译通过，66项自检通过，四页125%缩放WPF渲染图已检查，无底栏残留或卡片裁切。桌面托盘注册在本诊断宿主不可用，未将其描述为真实托盘点击实测。根目录EXE已更新。

## 统一 Dock 点击与拖拽松手的页面切换

WPF：点击ClickDockTab和拖拽松手FinishNavPillFluidSwipe最终均调用NavigateToPage。拖动只更新胶囊位移和pendingTab；松手使用相同320ms曲线吸附。目标与activePage一致时不调用ShowRightPage、不重播卡片。拖拽结束120ms内忽略Dock点击。新页面卡片严格按Y、X排序，统一从X=72、Opacity=0开始，420ms入场、55ms错峰；旧页立即隐藏，不交叉淡化。延迟卡片也预设X=72，消除首次与再次进入时的初始状态差异。

SVG：同步上述单一switchTab入口；移除pointerdown对页面动画的清理，拖动不改页面；松手前先清理捕获状态，防止lostpointercapture重复处理。相同档位只吸附；保留120ms点击拦截。脚本使用CDATA并转义文本中的&，使文件作为独立SVG也能正常解析和执行。

验证：编译与66项自检通过；WPF统一切换专项9项通过；松手清理专项5项通过；Chrome真实指针输入10项通过，包括点击/拖拽动画参数一致、拖动期间页面状态完全不变、同档位无重播、120ms拦截、胶囊/透镜320ms同曲线及独立SVG加载。原生动效打断诊断最终所有卡片X=0、Opacity=1，只有目标页面可见。

报告：artifacts/svg-refine/data/dock-unified-test.json、dock-release-test.json、svg-unified-test.json。WPF预览仍为不包含DWM的渲染图。保留用户此前确认的材质、颜色、Dock上下对齐、底栏移除和右上角×收起托盘行为。根目录EXE已更新。

## Dock 液态物理拖拽（替代此前硬跟手与固定320ms吸附）

- 底层文字保持深灰、静止；胶囊与透镜共用位移和形变，透镜内文字放大1.10倍并维持紫蓝色。WPF透镜内容以逆位移和逆缩放补偿，保持文字中心固定；Dock内部统一子像素坐标，消除125%缩放下的文字错位。
- 75ms指数跟随提供重量感；按实际移动速度平滑拉伸，纵向上限1.18，横向始终取倒数以保持面积。停止拖动后自动恢复。
- 碰撞边界依据玻璃外框内缘计算，独立于最后一个档位；越界位移最多6px，阻力递增。真正贴壁时纵向压缩最多7%，横向相应变宽。
- 普通点击和内部松手共用单调减速曲线，吸附时长按距离从170ms增加至420ms；无过冲。
- 仅松手时已经贴壁且被压扁，才选择轻微过冲回弹曲线，并增加70ms收尾。两类动画均从当前位置和形状续接，结束时严格恢复到目标档位及1×1形状。
- 拖动阶段仅改变Dock，不切换或淡化右侧页面；松手与点击仍调用同一个页面切换入口。同档位不重播页面，保留120ms防误触及卡片420ms/55ms错峰。
- 跟随循环只在拖动时运行，松手、失去捕获、失焦和关闭时停止；减少动态效果时跳过惯性与形变。SVG使用相同数学模型及requestAnimationFrame实现。

验证：编译通过；81项自检通过（新增15项物理模型检查）；WPF液态交互14项、统一切换9项、松手清理5项通过；Chrome实际鼠标输入的液态交互22项及统一切换10项通过。检查上下边界、速度形变、恢复、体积保持、两类吸附、同档位不重播、松手后不跟随、减少动态效果、透镜边界与文字中心、四页Dock对齐。SVG作为独立XML可解析并正常运行。WPF截图仍为不含DWM背景的渲染图。

脚本：experiments/verify-dock-liquid.ps1、experiments/verify-svg-liquid.mjs。报告：artifacts/svg-refine/data/dock-liquid-test.json、svg-liquid-test.json。保留现有材质、配色、底栏移除与右上角×收起托盘行为。根目录EXE已更新。

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace EW_Assistant.Services
{
    /// <summary>
    /// 图像命令联调所用的命令目录，固定由源码维护，不落地到本地配置文件。
    /// </summary>
    public static class UiCommandCatalogService
    {
        public static JObject BuildMouseOnlyCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v2",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "当 goal 表示打开桌面图标、文件资源项、文件夹或“此电脑”等资源对象时，优先使用 mouse.move_double_click。",
                    "当当前屏幕上已经存在可直接操作的可见入口时，优先使用 mouse.move_click；可见入口包括普通按钮、标签页、任务栏按钮、托盘入口、开始菜单项、桌面快捷方式、窗口缩略图、网页链接和其他单击即可触发的控件。",
                    "当 goal 表示弹出上下文菜单、右键菜单或更多操作时，优先使用 mouse.move_right_click。",
                    "当当前只是人工预览落点、悬停确认位置，且不希望触发任何动作时，使用 mouse.move。"
                },
                ["commandTypes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "mouse",
                        ["enabled"] = true,
                        ["description"] = "屏幕坐标鼠标命令。",
                        ["operationSelectionHint"] = "优先根据目标对象语义选择 operation：打开资源对象偏双击；按钮、任务栏、托盘入口、桌面快捷方式、窗口缩略图和菜单项偏单击；上下文菜单偏右击；纯预览偏移动。",
                        ["operations"] = new JArray
                        {
                            BuildMouseOperation(
                                "move",
                                "移动到目标点，不点击。",
                                new[]
                                {
                                    "仅用于人工预览落点或悬停确认位置。",
                                    "不会触发打开、选择或按钮点击。"
                                },
                                new[]
                                {
                                    "预览桌面图标位置",
                                    "预览任务栏按钮位置"
                                }),
                            BuildMouseOperation(
                                "move_click",
                                "移动到目标点后单击。",
                                new[]
                                {
                                    "适用于按钮、标签页、开始菜单项、任务栏按钮、网页链接等单击即可触发的目标。",
                                    "若 goal 是打开桌面图标或文件资源项，通常不应优先选择此操作。"
                                },
                                new[]
                                {
                                    "点击开始按钮",
                                    "点击任务栏资源管理器按钮",
                                    "点击保存按钮"
                                }),
                            BuildMouseOperation(
                                "move_double_click",
                                "移动到目标点后双击。",
                                new[]
                                {
                                    "适用于打开桌面图标、文件夹、文件资源项、此电脑等资源对象。",
                                    "当 goal 明确是“打开”且目标看起来像桌面图标或资源管理器列表项时，应优先考虑此操作。"
                                },
                                new[]
                                {
                                    "打开此电脑",
                                    "打开桌面上的文件夹",
                                    "打开资源管理器中的文件"
                                }),
                            BuildMouseOperation(
                                "move_right_click",
                                "移动到目标点后右击。",
                                new[]
                                {
                                    "适用于需要弹出右键菜单、上下文菜单或更多操作菜单的场景。"
                                },
                                new[]
                                {
                                    "打开桌面图标右键菜单",
                                    "查看文件的更多操作"
                                })
                        }
                    }
                }
            };
        }

        public static JObject BuildMouseOnlyCompactCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v2.compact",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "goal 表示打开桌面图标、文件夹、文件资源项或“此电脑”等资源对象时，优先使用 mouse.move_double_click。",
                    "goal 指向按钮、任务栏按钮、托盘入口、开始菜单项、桌面快捷方式、窗口缩略图、菜单项或链接时，优先使用 mouse.move_click。",
                    "goal 指向右键菜单、上下文菜单或更多操作时，优先使用 mouse.move_right_click。",
                    "当前只是人工预览落点、悬停确认位置时，使用 mouse.move。"
                },
                ["commandTypes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "mouse",
                        ["enabled"] = true,
                        ["operationSelectionHint"] = "打开资源对象偏双击；按钮、菜单、任务栏入口、托盘入口、桌面快捷方式和窗口缩略图偏单击；右键菜单偏右击；纯预览偏移动。",
                        ["operations"] = new JArray
                        {
                            BuildCompactMouseOperation(
                                "move",
                                "仅用于预览落点，不触发任何动作。"),
                            BuildCompactMouseOperation(
                                "move_click",
                                "适用于按钮、任务栏按钮、开始菜单项、菜单项等单击即可触发的目标。"),
                            BuildCompactMouseOperation(
                                "move_double_click",
                                "适用于桌面图标、文件夹、文件资源项、此电脑等需要打开资源对象的目标。"),
                            BuildCompactMouseOperation(
                                "move_right_click",
                                "适用于需要弹出右键菜单或上下文菜单的目标。")
                        }
                    }
                }
            };
        }

        public static JObject BuildCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v6",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "当当前屏幕上已经存在可直接操作的可见入口时，优先使用 mouse.move_click；可见入口包括按钮、菜单项、列表项、树节点、标签页、任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图和其他单击即可触发的导航入口。",
                    "当目标是文件、文件夹、磁盘、桌面图标或资源管理器中的资源对象，且通常需要打开进入时，优先使用 mouse.move_double_click。",
                    "当目标是输入框、搜索框、地址栏或编辑区域，且位置已明确或可先聚焦时，优先使用 keyboard.type_text、keyboard.key_press 或 keyboard.hotkey。",
                    "当使用 keyboard.type_text、keyboard.key_press 或 keyboard.hotkey，且已知目标程序或窗口标题时，应同时传 targetWindowTitleContains，让系统先激活目标窗口，再执行输入或按键。",
                    "不要仅凭截图看起来光标在输入区，就省略 targetWindowTitleContains；尤其是启动程序后立即输入文本时，更应显式指定窗口标题。",
                    "当已知或高度怀疑目标窗口已经存在，只是不在前台、被遮挡、被最小化或在后台时，优先使用 window.activate、window.find_best_match、window.wait_appear、window.wait_disappear；window.activate 默认会尽量最大化目标窗口。",
                    "当目标是打开已知路径、已知程序或已知网址，且无需先做视觉定位时，优先使用 shell.open_path、shell.launch_app、shell.open_url；这些打开类命令默认会尽量最大化新窗口。",
                    "当目标是创建目录、创建文件、重命名或直接落盘，且目标路径已知时，优先使用 file 或 explorer，不要退回到 execute.click、mouse 或右键菜单。",
                    "shell.open_path 的 commandLine 必须是现有文件路径、目录路径或 shell: 路径，例如 D:\\\\、D:\\\\DataAI、shell:Downloads；不要写 explorer D:、start D: 这类命令行。",
                    "shell.launch_app 的 commandLine 只填写程序名、exe 路径或可直接启动的应用标识，例如 explorer.exe、notepad.exe；不要在里面混入打开路径参数。",
                    "shell.open_url 的 commandLine 只填写 http/https 绝对网址，不要在前面拼接浏览器命令。",
                    "只有在需要冷启动，或当前既没有可见入口也没有可激活窗口线索时，才使用 app.open_or_activate；如果当前屏幕已有任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图或其他可见入口，应优先点击或先查找/激活现有窗口；不确定程序名时可先 app.list_installed 或 app.resolve。",
                    "当任务是用指定浏览器打开网址、切换浏览器或搜索关键词时，优先使用 browser.open_or_activate、browser.open_url、browser.open_url_in_tab、browser.search_web、browser.search_site。",
                    "当文本较长、输入法不稳定或需要稳定粘贴时，优先使用 clipboard.set_text 或 clipboard.paste_text。",
                    "当任务关键在于确认结果是否真的达成，例如窗口是否已出现、文本是否真的可见、消息是否真的发送成功时，优先使用 verify。",
                    "当任务需要直接处理目录、文件选中、创建文件夹、文本落盘或重命名时，优先使用 explorer 或 file，不要退化成鼠标点菜单。",
                    "当需要先了解当前窗口现场而不是立即操作时，优先使用 window.list 或 window.find_best_match。",
                    "当当前只是人工预览落点、悬停确认位置，且不希望触发任何动作时，使用 mouse.move。"
                },
                ["commandTypes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "mouse",
                        ["enabled"] = true,
                        ["description"] = "屏幕坐标鼠标命令。",
                        ["operationSelectionHint"] = "优先根据目标对象语义选择 operation：打开资源对象偏双击；按钮、任务栏、托盘入口、桌面快捷方式、窗口缩略图和菜单项偏单击；上下文菜单偏右击；纯预览偏移动。",
                        ["operations"] = new JArray
                        {
                            BuildMouseOperation(
                                "move",
                                "移动到目标点，不点击。",
                                new[]
                                {
                                    "仅用于人工预览落点或悬停确认位置。",
                                    "不会触发打开、选择或按钮点击。"
                                },
                                new[]
                                {
                                    "预览桌面图标位置",
                                    "预览任务栏按钮位置"
                                }),
                            BuildMouseOperation(
                                "move_click",
                                "移动到目标点后单击。",
                                new[]
                                {
                                    "适用于按钮、标签页、开始菜单项、任务栏按钮、网页链接等单击即可触发的目标。",
                                    "若 goal 是打开桌面图标或文件资源项，通常不应优先选择此操作。"
                                },
                                new[]
                                {
                                    "点击开始按钮",
                                    "点击任务栏资源管理器按钮",
                                    "点击保存按钮"
                                }),
                            BuildMouseOperation(
                                "move_double_click",
                                "移动到目标点后双击。",
                                new[]
                                {
                                    "适用于打开桌面图标、文件夹、文件资源项、此电脑等资源对象。",
                                    "当 goal 明确是“打开”且目标看起来像桌面图标或资源管理器列表项时，应优先考虑此操作。"
                                },
                                new[]
                                {
                                    "打开此电脑",
                                    "打开桌面上的文件夹",
                                    "打开资源管理器中的文件"
                                }),
                            BuildMouseOperation(
                                "move_right_click",
                                "移动到目标点后右击。",
                                new[]
                                {
                                    "适用于需要弹出右键菜单、上下文菜单或更多操作菜单的场景。"
                                },
                                new[]
                                {
                                    "打开桌面图标右键菜单",
                                    "查看文件的更多操作"
                                })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "keyboard",
                        ["enabled"] = true,
                        ["description"] = "键盘输入与快捷键命令。已知目标窗口时，优先显式传 targetWindowTitleContains，让系统先聚焦再输入。",
                        ["operationSelectionHint"] = "输入文本用 type_text；单个按键或方向键/Enter/Esc 用 key_press；组合快捷键用 hotkey。已知目标窗口时，优先同时传 targetWindowTitleContains。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "type_text",
                                "向当前前台窗口或目标窗口输入文本。",
                                new[]
                                {
                                    "适用于搜索框、地址栏、输入框、表单控件等可直接输入文本的场景。",
                                    "若已知目标窗口标题，应传 targetWindowTitleContains 先聚焦目标窗口。",
                                    "不要仅凭截图推断焦点已在输入区，尤其是启动记事本、资源管理器或浏览器后立即输入时。",
                                    "当目标是“打开记事本并输入 123456”这类任务，推荐 targetWindowTitleContains=记事本。"
                                },
                                new[]
                                {
                                    "在资源管理器地址栏输入 D:\\",
                                    "在开始菜单搜索框输入 记事本",
                                    "向标题包含 记事本 的窗口输入 123456"
                                },
                                new[] { "text" },
                                new[] { "targetWindowTitleContains", "lane" }),
                            BuildOperation(
                                "key_press",
                                "按下一个或多个按键，可选配合修饰键。",
                                new[]
                                {
                                    "适用于 Enter、Esc、Tab、方向键、F1-F12 等单键动作。",
                                    "若同时给出 modifiers，会先按下修饰键，再依次按 keys。",
                                    "若已知目标窗口标题，应同时传 targetWindowTitleContains，避免按键打到错误窗口。"
                                },
                                new[]
                                {
                                    "按 Enter 确认",
                                    "按 Down 选择下一项",
                                    "向标题包含 记事本 的窗口按 Enter"
                                },
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "lane" }),
                            BuildOperation(
                                "hotkey",
                                "执行组合快捷键。",
                                new[]
                                {
                                    "适用于 Ctrl+L、Alt+F4、Win+R、Ctrl+Shift+Esc 等快捷键。",
                                    "修饰键通过 modifiers 传入，动作键通过 keys 传入。",
                                    "若快捷键依赖某个已知窗口为前台，应同时传 targetWindowTitleContains。"
                                },
                                new[]
                                {
                                    "Ctrl+L 聚焦地址栏",
                                    "Win+R 打开运行",
                                    "向标题包含 资源管理器 的窗口发送 Ctrl+L"
                                },
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "window",
                        ["enabled"] = true,
                        ["description"] = "窗口级控制与等待命令。",
                        ["operationSelectionHint"] = "已知窗口已存在但不在前台时用 activate；等待出现/消失用 wait_appear、wait_disappear；关闭窗口用 close；先确认现有窗口是否存在用 list 或 find_best_match。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "activate",
                                "按窗口标题关键字激活目标窗口。",
                                new[]
                                {
                                    "适用于目标窗口已经存在，只是不在前台、被遮挡、被最小化或需要先唤醒时。",
                                    "适用于先把资源管理器、浏览器、程序主窗口切到前台后再执行键盘或鼠标动作。"
                                },
                                new[]
                                {
                                    "激活标题包含 资源管理器 的窗口"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "wait_appear",
                                "等待指定标题关键字的窗口出现。",
                                new[]
                                {
                                    "适用于动作后等待窗口打开，再进入下一步。"
                                },
                                new[]
                                {
                                    "等待标题包含 运行 的窗口出现"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "wait_disappear",
                                "等待指定标题关键字的窗口消失。",
                                new[]
                                {
                                    "适用于等待弹窗关闭或等待某个加载窗口退出。"
                                },
                                new[]
                                {
                                    "等待标题包含 正在加载 的窗口消失"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "close",
                                "按窗口标题关键字关闭目标窗口。",
                                new[]
                                {
                                    "属于高风险动作，需显式传 confirmed=true。",
                                    "仅在明确要关闭当前窗口时使用。"
                                },
                                new[]
                                {
                                    "关闭标题包含 记事本 的窗口"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "confirmed", "timeoutMs", "pollMs", "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "shell",
                        ["enabled"] = true,
                        ["description"] = "低风险 Shell 打开类命令。open_path 打开现有路径，不执行命令行；launch_app 启动程序本体；open_url 打开明确网址；默认尽量最大化新窗口。",
                        ["operationSelectionHint"] = "路径本体用 open_path；程序本体用 launch_app；网址本体用 open_url。默认尽量最大化新窗口。不要把 explorer D:\\\\ 这类命令行塞进 open_path 或 launch_app。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "open_path",
                                "打开已知文件、目录或 shell: 路径；不是命令行执行器；默认尽量最大化新窗口。",
                                new[]
                                {
                                    "适用于已知 D:\\、某目录路径或某个文件路径时直接打开。",
                                    "优先用于无需先做视觉定位的确定路径。",
                                    "commandLine 必须直接填写路径本体，例如 D:\\、D:\\DataAI、shell:Downloads。",
                                    "不要填写 explorer D:\\、start D:\\、cmd /c start D:\\。"
                                },
                                new[]
                                {
                                    "打开 D:\\",
                                    "打开 D:\\DataAI",
                                    "打开 shell:Downloads"
                                },
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildOperation(
                                "launch_app",
                                "启动已知程序或可执行文件；默认尽量最大化新窗口。",
                                new[]
                                {
                                    "适用于启动 explorer.exe、notepad.exe 等已知程序。",
                                    "commandLine 只填写程序名、exe 路径或可直接启动的应用标识。",
                                    "不要填写 explorer D:\\、notepad.exe a.txt 这类带参数命令行。"
                                },
                                new[]
                                {
                                    "启动 notepad.exe",
                                    "启动 explorer.exe"
                                },
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildOperation(
                                "open_url",
                                "打开已知 http/https 网址；默认尽量最大化浏览器窗口。",
                                new[]
                                {
                                    "适用于在默认浏览器中打开明确网址。",
                                    "commandLine 只填写完整网址本体，例如 https://www.openai.com。",
                                    "不要填写 chrome https://... 或 start https://...。"
                                },
                                new[]
                                {
                                    "打开 https://www.openai.com"
                                },
                                new[] { "commandLine" },
                                new[] { "lane" })
                        }
                    },
                    BuildAppCommandType(compact: false),
                    BuildBrowserCommandType(compact: false),
                    BuildClipboardCommandType(compact: false),
                    BuildVerifyCommandType(compact: false),
                    BuildExplorerCommandType(compact: false),
                    BuildFileCommandType(compact: false)
                }
            };
        }

        public static JObject BuildCompactCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v6.compact",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "已知路径、程序或网址且无需视觉定位时优先 shell；打开类命令默认尽量最大化新窗口。",
                    "已知窗口标题且需要聚焦或等待时优先 window；其中 window.activate 默认尽量最大化目标窗口。",
                    "已知文件系统目标路径时，创建目录/创建文件/重命名/落盘优先 file 或 explorer，不要退回到鼠标点菜单。",
                    "输入文本、确认键、方向键或快捷键时优先 keyboard。",
                    "使用 keyboard 时，如果已知目标窗口标题，优先同时传 targetWindowTitleContains，不要只凭视觉猜焦点。",
                    "shell.open_path 的 commandLine 只能是路径本体或 shell: 路径，不要写 explorer D: 或 start D:。",
                    "shell.launch_app 的 commandLine 只能是程序本体，不要带路径参数；shell.open_url 的 commandLine 只能是 http/https 绝对网址。",
                    "只有在需要冷启动，或当前既没有可见入口也没有稳定窗口线索时，才使用 app.open_or_activate；如果当前屏幕已有任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图或其他可见入口，应优先点击或先查找/激活现有窗口；不确定程序名时可先 app.list_installed 或 app.resolve。",
                    "指定浏览器打开网址、切换浏览器或搜索关键词时优先 browser.open_or_activate、browser.open_url、browser.open_url_in_tab、browser.search_web、browser.search_site。",
                    "长文本、口令或输入法不稳定时优先 clipboard.set_text 或 clipboard.paste_text。",
                    "需要确认窗口、文本或发送结果时优先 verify。",
                    "需要处理目录、文件选中、创建文件夹、文本落盘或重命名时优先 explorer 或 file，不要退化成鼠标点菜单。",
                    "需要先枚举或查找窗口时优先 window.list 或 window.find_best_match。",
                    "只有必须按当前截图点击具体控件时才使用 mouse。"
                },
                ["commandTypes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "shell",
                        ["operationSelectionHint"] = "路径本体 open_path；程序本体 launch_app；网址本体 open_url。默认尽量最大化新窗口。不要把 explorer D:\\\\ 这类命令行塞进 shell。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "open_path",
                                "commandLine 直接填现有路径或 shell: 路径，例如 D:\\\\、D:\\\\DataAI、shell:Downloads；默认尽量最大化新窗口；不要写 explorer D:。",
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildCompactOperation(
                                "launch_app",
                                "commandLine 直接填程序名或 exe 路径，例如 explorer.exe、notepad.exe；默认尽量最大化新窗口；不要混入参数。",
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildCompactOperation(
                                "open_url",
                                "commandLine 直接填 http/https 绝对网址；默认尽量最大化浏览器窗口；不要拼浏览器命令。",
                                new[] { "commandLine" },
                                new[] { "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "window",
                        ["operationSelectionHint"] = "已知窗口已存在但不在前台时用 activate；activate 默认尽量最大化目标窗口；等待出现/消失 wait_appear/wait_disappear；关闭 close；先确认现有窗口是否存在用 list 或 find_best_match。",
                        ["operations"] = new JArray
                        {
                            BuildMinimalOperation("activate", new[] { "targetWindowTitleContains" }),
                            BuildMinimalOperation("wait_appear", new[] { "targetWindowTitleContains" }),
                            BuildMinimalOperation("wait_disappear", new[] { "targetWindowTitleContains" }),
                            BuildMinimalOperation("close", new[] { "targetWindowTitleContains", "confirmed" }),
                            BuildMinimalOperation("list", Array.Empty<string>()),
                            BuildMinimalOperation("find_best_match", new[] { "targetWindowTitleContains" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "keyboard",
                        ["operationSelectionHint"] = "文本 type_text；快捷键 hotkey；单键 key_press。已知目标窗口时优先带 targetWindowTitleContains。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "type_text",
                                "text 填要输入的内容；已知目标窗口时优先同时传 targetWindowTitleContains，例如向记事本输入 123456。",
                                new[] { "text" },
                                new[] { "targetWindowTitleContains", "lane" }),
                            BuildCompactOperation(
                                "key_press",
                                "keys 填按键；若已知目标窗口，优先同时传 targetWindowTitleContains，避免按到错误窗口。",
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "lane" }),
                            BuildCompactOperation(
                                "hotkey",
                                "keys+modifiers 组成快捷键；若快捷键要发给特定窗口，优先同时传 targetWindowTitleContains。",
                                new[] { "keys", "modifiers" },
                                new[] { "targetWindowTitleContains", "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "mouse",
                        ["operationSelectionHint"] = "按钮、任务栏入口、托盘入口、桌面快捷方式和窗口缩略图单击；资源对象双击；右键菜单右击；仅预览 move。",
                        ["operations"] = new JArray
                        {
                            BuildMinimalOperation("move", new[] { "point_2d" }),
                            BuildMinimalOperation("move_click", new[] { "point_2d" }),
                            BuildMinimalOperation("move_double_click", new[] { "point_2d" }),
                            BuildMinimalOperation("move_right_click", new[] { "point_2d" })
                        }
                    },
                    BuildAppCommandType(compact: true),
                    BuildBrowserCommandType(compact: true),
                    BuildClipboardCommandType(compact: true),
                    BuildVerifyCommandType(compact: true),
                    BuildExplorerCommandType(compact: true),
                    BuildFileCommandType(compact: true)
                }
            };
        }

        public static JObject BuildGroundedCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v9.grounded.light",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "每轮默认先调用 /runtime/observe，并优先使用 detailLevel=decision_light；只有歧义很大、动作连续失败或需要排障时，才升级到 detailLevel=full。",
                    "决策时优先读取 observe 返回的 decisionContext、foregroundWindow、candidateBriefs 与 candidates；topWindows 和 OCR 长文本只作为辅助证据。",
                    "当前界面已经存在可稳定命中的目标时，优先使用 execute；任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图等可见入口也属于可直接操作目标，不要在已有入口时退回到重新启动应用。",
                    "execute.args.targetId 必须来自 observe 返回的 candidates[].targetId，禁止编造。",
                    "优先选择 stableTarget=true、visible=true、enabled=true，且 availableActions 明确包含当前 operation 的候选目标。",
                    "execute.set_text 仅用于 supportsSetText=true 的输入型目标，并且必须同时提供 text。",
                    "当任务是向已知程序或已知窗口输入文本，但当前 observe 暂时没有给出 supportsSetText=true 的输入控件时，可以退回到 window.wait_appear/window.activate + keyboard.focus_type_text。",
                    "启动程序后如果下一步就是输入文本，优先 shell.launch_app 后接 window.wait_appear，再用 keyboard.focus_type_text，并显式传 targetWindowTitleContains。",
                    "已知路径、程序或网址且无需视觉定位时，才使用 shell.open_path、shell.launch_app、shell.open_url。",
                    "shell 适合启动或打开确定对象；进入界面后，后续交互优先回到 execute，不要继续让模型输出标题猜测。",
                    "只有在需要冷启动，或当前既没有可见入口也没有稳定窗口线索时，才使用 app.open_or_activate；如果当前屏幕已有任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图或其他可见入口，应优先点击或先查找/激活现有窗口；不确定程序名时可先 app.list_installed 或 app.resolve。",
                    "当任务是用指定浏览器打开网址、切换浏览器或搜索关键词时，优先使用 browser.open_or_activate、browser.open_url、browser.open_url_in_tab、browser.search_web、browser.search_site，不要拆成手工点地址栏再输入。",
                    "当文本较长、输入法不稳定或需要稳定粘贴时，优先使用 clipboard.set_text 或 clipboard.paste_text。",
                    "当关键在于确认界面结果是否真的达成时，优先使用 verify，不要仅凭历史动作推断完成。",
                    "当目标是文件资源、目录打开、文件选中或文本落盘时，优先使用 explorer 与 file，不要拆成多轮手工点击保存框。",
                    "当当前窗口场景不清楚时，优先先做 window.list 或 window.find_best_match，再决定 activate 或 wait。",
                    "OCR 中的长段说明、日志、状态文本不要直接当成动作目标；若 observe 已做降噪，仍应以 candidateBriefs/candidates 中的稳定目标为准。",
                    "close 属于高风险动作，只有目标明确且后果可接受时才允许，并且必须传 confirmed=true。"
                },
                ["commandTypes"] = new JArray
                {
                    new JObject
                    {
                        ["name"] = "execute",
                        ["enabled"] = true,
                        ["description"] = "基于 /runtime/observe 候选目标的稳定执行命令。由 workflow 注入 runtimeId，并按 targetId 落到 refId/selector/hwnd。",
                        ["operationSelectionHint"] = "窗口激活用 activate；按钮和常规控件用 click；资源对象进入用 double_click；上下文菜单用 right_click；输入框写入用 set_text；关闭窗口用 close。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "activate",
                                "激活 observe 返回的目标窗口或可聚焦目标。",
                                new[]
                                {
                                    "targetId 必须来自当前 observe 结果。",
                                    "优先用于把目标窗口切到前台，或明确需要把某个 grounded 目标置为焦点。"
                                },
                                new[]
                                {
                                    "激活 E001 对应的资源管理器窗口"
                                },
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildOperation(
                                "click",
                                "对 grounded 目标执行单击。",
                                new[]
                                {
                                    "适用于按钮、菜单项、标签页、列表项、树节点等单击即可触发的候选目标。",
                                    "仅在 availableActions 包含 click 时选择。"
                                },
                                new[]
                                {
                                    "单击 E005 对应的开始菜单项"
                                },
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildOperation(
                                "double_click",
                                "对 grounded 目标执行双击。",
                                new[]
                                {
                                    "适用于文件、文件夹、磁盘、资源管理器项等需要进入或打开的资源对象。",
                                    "仅在 availableActions 包含 double_click 时选择。"
                                },
                                new[]
                                {
                                    "双击 E008 对应的 本地磁盘 (D:) 项"
                                },
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildOperation(
                                "right_click",
                                "对 grounded 目标执行右击。",
                                new[]
                                {
                                    "适用于需要弹出右键菜单或上下文菜单的目标。",
                                    "仅在 availableActions 包含 right_click 时选择。"
                                },
                                new[]
                                {
                                    "右击 E011 对应的文件项"
                                },
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildOperation(
                                "set_text",
                                "向 grounded 输入型目标直接写入文本。",
                                new[]
                                {
                                    "仅当目标 supportsSetText=true 时使用。",
                                    "优先用于地址栏、搜索框、输入框、编辑区等稳定输入控件。"
                                },
                                new[]
                                {
                                    "向 E003 对应的记事本编辑区写入 123456",
                                    "向 E006 对应的资源管理器地址栏写入 D:\\"
                                },
                                new[] { "targetId", "text" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildOperation(
                                "close",
                                "关闭 grounded 目标窗口。",
                                new[]
                                {
                                    "属于高风险动作，必须显式传 confirmed=true。",
                                    "仅在当前目标明确是可关闭窗口时使用。"
                                },
                                new[]
                                {
                                    "关闭 E001 对应的窗口"
                                },
                                new[] { "targetId", "confirmed" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "window",
                        ["enabled"] = true,
                        ["description"] = "窗口级兜底控制。当 grounded 目标暂未出现，但已知程序标题时，用于等待、激活或关闭目标窗口。",
                        ["operationSelectionHint"] = "已知窗口已存在但不在前台时用 activate；等待出现/消失用 wait_appear、wait_disappear；关闭窗口用 close；先确认现有窗口是否存在用 list 或 find_best_match。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "activate",
                                "按标题关键字激活目标窗口。",
                                new[]
                                {
                                    "当 grounded 候选里还没出现可输入控件，但已知程序窗口标题时使用。",
                                    "适用于目标窗口已经存在，只是不在前台、被遮挡、被最小化或需要先唤醒时。"
                                },
                                new[]
                                {
                                    "激活标题包含 记事本 的窗口"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "wait_appear",
                                "等待指定标题关键字的窗口出现。",
                                new[]
                                {
                                    "适用于 shell.launch_app 之后等待程序主窗口真正出现。"
                                },
                                new[]
                                {
                                    "等待标题包含 记事本 的窗口出现"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "wait_disappear",
                                "等待指定标题关键字的窗口消失。",
                                new[]
                                {
                                    "适用于关闭动作后做窗口级验收。"
                                },
                                new[]
                                {
                                    "等待标题包含 记事本 的窗口关闭"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "close",
                                "关闭目标窗口。",
                                new[]
                                {
                                    "属于高风险动作，必须显式传 confirmed=true。"
                                },
                                new[]
                                {
                                    "关闭标题包含 记事本 的窗口"
                                },
                                new[] { "targetWindowTitleContains", "confirmed" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "list",
                                "枚举当前可见顶层窗口。",
                                new[]
                                {
                                    "适用于当前窗口场景不清楚，需要先了解现场再决策。"
                                },
                                new[]
                                {
                                    "列出当前窗口"
                                },
                                Array.Empty<string>(),
                                new[] { "lane" }),
                            BuildOperation(
                                "find_best_match",
                                "按标题关键字查找当前最匹配的现有顶层窗口。",
                                new[]
                                {
                                    "适用于已知窗口名字，但不确定当前是否存在、是否在后台或标题是否带后缀时。",
                                    "未命中只表示当前顶层窗口未匹配，不等于应用未运行。"
                                },
                                new[]
                                {
                                    "查找最匹配的 微信 窗口"
                                },
                                new[] { "targetWindowTitleContains" },
                                new[] { "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "keyboard",
                        ["enabled"] = true,
                        ["description"] = "窗口级键盘兜底命令。适用于已知窗口标题、但 grounded 输入控件暂未识别出来的场景。",
                        ["operationSelectionHint"] = "已知目标窗口且要尽量减少焦点干扰时，优先 focus_type_text；普通文本输入可用 type_text；单键 key_press；组合键 hotkey。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "focus_type_text",
                                "先激活目标窗口并尽量聚焦输入区域，再输入文本。",
                                new[]
                                {
                                    "适用于启动记事本、启动资源管理器后立即输入文本等容易被焦点干扰的场景。",
                                    "必须同时提供 text 与 targetWindowTitleContains。",
                                    "当 observe 暂时没识别出编辑区，但已知窗口标题时，优先使用它。"
                                },
                                new[]
                                {
                                    "向标题包含 记事本 的窗口聚焦后输入 123456"
                                },
                                new[] { "text", "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "type_text",
                                "激活目标窗口后输入文本。",
                                new[]
                                {
                                    "适用于已知窗口标题，且不需要额外点击输入区即可接收文本的场景。",
                                    "若担心程序启动后焦点不稳定，优先改用 focus_type_text。"
                                },
                                new[]
                                {
                                    "向标题包含 记事本 的窗口输入 123456"
                                },
                                new[] { "text" },
                                new[] { "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "key_press",
                                "向目标窗口发送一个或多个按键。",
                                new[]
                                {
                                    "适用于 Enter、Esc、Tab、方向键等。"
                                },
                                new[]
                                {
                                    "向标题包含 记事本 的窗口按 Enter"
                                },
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" }),
                            BuildOperation(
                                "hotkey",
                                "向目标窗口发送组合快捷键。",
                                new[]
                                {
                                    "适用于 Ctrl+L、Ctrl+S、Alt+F4 等快捷键。"
                                },
                                new[]
                                {
                                    "向标题包含 资源管理器 的窗口发送 Ctrl+L"
                                },
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" })
                        }
                    },
                    new JObject
                    {
                        ["name"] = "shell",
                        ["enabled"] = true,
                        ["description"] = "低风险 Shell 打开类命令。用于打开已知路径、程序或网址，不依赖当前视觉目标。",
                        ["operationSelectionHint"] = "路径本体 open_path；程序本体 launch_app；网址本体 open_url。进入界面后，后续优先回到 execute。",
                        ["operations"] = new JArray
                        {
                            BuildOperation(
                                "open_path",
                                "打开已知文件、目录或 shell: 路径；不是命令行执行器。",
                                new[]
                                {
                                    "commandLine 必须直接填写路径本体，例如 D:\\、D:\\DataAI、shell:Downloads。",
                                    "不要填写 explorer D:\\、start D:\\、cmd /c start D:\\。"
                                },
                                new[]
                                {
                                    "打开 D:\\",
                                    "打开 shell:Downloads"
                                },
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildOperation(
                                "launch_app",
                                "启动已知程序或可执行文件。",
                                new[]
                                {
                                    "commandLine 只填写程序名、exe 路径或可直接启动的应用标识。",
                                    "不要填写 explorer D:\\、notepad.exe a.txt 这类带参数命令行。"
                                },
                                new[]
                                {
                                    "启动 notepad.exe",
                                    "启动 explorer.exe"
                                },
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildOperation(
                                "open_url",
                                "打开已知 http/https 网址。",
                                new[]
                                {
                                    "commandLine 只填写完整网址本体，例如 https://www.openai.com。",
                                    "不要在前面拼接浏览器命令。"
                                },
                                new[]
                                {
                                    "打开 https://www.openai.com"
                                },
                                new[] { "commandLine" },
                                new[] { "lane" })
                        }
                    },
                    BuildAppCommandType(compact: false),
                    BuildBrowserCommandType(compact: false),
                    BuildClipboardCommandType(compact: false),
                    BuildVerifyCommandType(compact: false),
                    BuildExplorerCommandType(compact: false),
                    BuildFileCommandType(compact: false)
                }
            };
        }

        public static JObject BuildGroundedCompactCatalog()
        {
            return new JObject
            {
                ["version"] = "ui.command.catalog.v9.grounded.light.compact",
                ["source"] = "code",
                ["selectionRules"] = new JArray
                {
                    "默认先 observe(decision_light)，仅在排障或连续失败时升 full。",
                    "先看 decisionContext、candidateBriefs、candidates；长 OCR 只作辅助证据。",
                    "已有稳定候选或可见入口时优先 execute；已知路径/程序/网址且无需视觉定位时才用 shell；shell/window.activate/app/browser/explorer 这类显式打开或切窗命令默认会尽量最大化窗口。",
                    "已知文件系统目标路径时，创建目录、创建文件、重命名、保存优先 file 或 explorer；不要因为屏幕上看得见资源管理器就退化成 execute.click 或 mouse。",
                    "execute.targetId 必须来自 observe.candidates；优先 stableTarget=true 且 availableActions 包含当前 operation。",
                    "输入框优先 execute.set_text；无可写控件时退回 window.activate/wait_appear + keyboard.focus_type_text。",
                    "只有在既没有可见入口也没有稳定窗口线索时，才用 app.open_or_activate；查窗失败一次不等于未运行。",
                    "需要确认窗口、文本或发送结果时优先 verify；verify.text_visible 只表示一般文本可见，不等于消息已发送；处理目录/文件、创建文件夹、重命名时优先 explorer 或 file，不要退化成 execute.click 或 mouse。"
                },
                ["commandTypes"] = new JArray
                {
                    SlimCompactCommandType(new JObject
                    {
                        ["name"] = "execute",
                        ["operationSelectionHint"] = "窗口激活 activate；按钮控件单击 click；资源对象进入 double_click；上下文菜单 right_click；输入框写入 set_text；关闭窗口 close。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "activate",
                                "targetId 取自 observe；用于把 grounded 目标切到前台。",
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildCompactOperation(
                                "click",
                                "targetId 取自 observe；仅在 availableActions 包含 click 时使用。",
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildCompactOperation(
                                "double_click",
                                "targetId 取自 observe；用于磁盘、文件夹、资源项等进入动作。",
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildCompactOperation(
                                "right_click",
                                "targetId 取自 observe；用于右键菜单。",
                                new[] { "targetId" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildCompactOperation(
                                "set_text",
                                "targetId 取自 observe，且目标必须 supportsSetText=true；同时提供 text。",
                                new[] { "targetId", "text" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" }),
                            BuildCompactOperation(
                                "close",
                                "targetId 取自 observe；高风险，必须 confirmed=true。",
                                new[] { "targetId", "confirmed" },
                                new[] { "runtimeId", "lane", "idempotencyKey", "retries", "verifyTimeoutMs", "verifyTitleContains" })
                        }
                    }),
                    SlimCompactCommandType(new JObject
                    {
                        ["name"] = "window",
                        ["operationSelectionHint"] = "已知窗口已存在但不在前台时用 activate；activate 默认尽量最大化目标窗口；等待出现/消失用 wait_appear、wait_disappear；关闭窗口用 close；先确认现有窗口是否存在用 list 或 find_best_match。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "activate",
                                "targetWindowTitleContains 填已知窗口标题关键字，用于把已存在但不在前台的目标窗口切到前台，并默认尽量最大化。",
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "wait_appear",
                                "shell.launch_app 后常用；等待已知窗口标题出现。",
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "wait_disappear",
                                "等待已知窗口标题消失。",
                                new[] { "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "close",
                                "高风险；targetWindowTitleContains 指目标窗口，且必须 confirmed=true。",
                                new[] { "targetWindowTitleContains", "confirmed" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "list",
                                "枚举当前可见顶层窗口。",
                                Array.Empty<string>(),
                                new[] { "lane" }),
                            BuildCompactOperation(
                                "find_best_match",
                                "按标题关键字查找当前最匹配的现有顶层窗口；未命中不等于应用未运行。",
                                new[] { "targetWindowTitleContains" },
                                new[] { "lane" })
                        }
                    }),
                    SlimCompactCommandType(new JObject
                    {
                        ["name"] = "keyboard",
                        ["operationSelectionHint"] = "焦点容易被抢时优先 focus_type_text；普通文本输入用 type_text；单键 key_press；快捷键 hotkey。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "focus_type_text",
                                "先激活目标窗口并尽量聚焦输入区域，再输入文本；适合“打开记事本后立即输入 123456”。",
                                new[] { "text", "targetWindowTitleContains" },
                                new[] { "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "type_text",
                                "text 填输入内容；已知目标窗口时优先带 targetWindowTitleContains。",
                                new[] { "text" },
                                new[] { "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "key_press",
                                "keys 填按键；若要发给特定窗口，优先同时传 targetWindowTitleContains。",
                                new[] { "keys" },
                                new[] { "modifiers", "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" }),
                            BuildCompactOperation(
                                "hotkey",
                                "keys+modifiers 组成快捷键；若依赖特定窗口为前台，优先同时传 targetWindowTitleContains。",
                                new[] { "keys", "modifiers" },
                                new[] { "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" })
                        }
                    }),
                    SlimCompactCommandType(new JObject
                    {
                        ["name"] = "shell",
                        ["operationSelectionHint"] = "路径本体 open_path；程序本体 launch_app；网址本体 open_url；默认尽量最大化新窗口。",
                        ["operations"] = new JArray
                        {
                            BuildCompactOperation(
                                "open_path",
                                "commandLine 直接填路径本体或 shell: 路径；默认尽量最大化新窗口；不要写 explorer D:。",
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildCompactOperation(
                                "launch_app",
                                "commandLine 直接填程序名或 exe 路径，例如 notepad.exe；默认尽量最大化新窗口；不要混入参数。",
                                new[] { "commandLine" },
                                new[] { "workingDirectory", "lane" }),
                            BuildCompactOperation(
                                "open_url",
                                "commandLine 直接填 http/https 绝对网址；默认尽量最大化浏览器窗口；不要拼浏览器命令。",
                                new[] { "commandLine" },
                                new[] { "lane" })
                        }
                    }),
                    SlimCompactCommandType(BuildAppCommandType(compact: true)),
                    SlimCompactCommandType(BuildBrowserCommandType(compact: true)),
                    SlimCompactCommandType(BuildClipboardCommandType(compact: true)),
                    SlimCompactCommandType(BuildVerifyCommandType(compact: true)),
                    SlimCompactCommandType(BuildExplorerCommandType(compact: true)),
                    SlimCompactCommandType(BuildFileCommandType(compact: true))
                }
            };
        }

        private static JObject BuildMouseOperation(
            string name,
            string description,
            IEnumerable<string> usageHints,
            IEnumerable<string> examples)
        {
            return BuildOperation(
                name,
                description,
                usageHints,
                examples,
                new[] { "point_2d" },
                new[]
                {
                    "lane",
                    "captureLeft",
                    "captureTop",
                    "captureWidth",
                    "captureHeight"
                });
        }

        private static JObject BuildCompactMouseOperation(string name, string usageHint)
        {
            return BuildCompactOperation(
                name,
                usageHint,
                new[] { "point_2d" },
                new[]
                {
                    "lane",
                    "captureLeft",
                    "captureTop",
                    "captureWidth",
                    "captureHeight"
                });
        }

        private static JObject BuildAppCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "app",
                ["enabled"] = true,
                ["description"] = compact ? null : "应用发现与冷启动兜底命令。用于枚举已安装应用、解析应用路径，以及在没有可见入口或稳定窗口线索时按应用名打开或激活应用。",
                ["operationSelectionHint"] = "只有在需要冷启动，或当前既没有可见入口也没有稳定窗口线索时，才用 open_or_activate；打开或激活后默认尽量最大化窗口；不确定程序名时先 list_installed 或 resolve。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "list_installed",
                            "枚举已安装应用；可选 appName 作为过滤词，maxResults 控制返回数量。",
                            Array.Empty<string>(),
                            new[] { "appName", "maxResults", "lane" })
                        : BuildOperation(
                            "list_installed",
                            "枚举当前系统的已安装应用记录，返回显示名、路径、进程名与别名。",
                            new[]
                            {
                                "当不知道程序的准确可执行名时，先用它获取候选应用列表。",
                                "可选传 appName 作为过滤词，maxResults 控制返回数量。"
                            },
                            new[]
                            {
                                "枚举与 360 浏览器 相关的已安装应用"
                            },
                            Array.Empty<string>(),
                            new[] { "appName", "maxResults", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "resolve",
                            "按 appName 解析应用路径与候选别名。",
                            new[] { "appName" },
                            new[] { "maxResults", "lane" })
                        : BuildOperation(
                            "resolve",
                            "按应用名解析最可能的应用路径，并返回候选列表。",
                            new[]
                            {
                                "适用于模型只知道自然语言应用名，不知道实际 exe 名时。",
                                "会综合系统注册表、别名和已知安装记录返回候选结果。"
                            },
                            new[]
                            {
                                "解析 360浏览器"
                            },
                            new[] { "appName" },
                            new[] { "maxResults", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "open_or_activate",
                            "按 appName 打开或激活应用；仅在没有可见入口或稳定窗口线索时使用；已运行则优先激活，未运行才尝试启动；默认尽量最大化窗口。",
                            new[] { "appName" },
                            new[] { "workingDirectory", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_or_activate",
                            "按应用名打开或激活目标应用。",
                            new[]
                            {
                                "仅当需要冷启动，或当前没有任务栏按钮、托盘入口、桌面快捷方式、窗口缩略图及其他可直接操作入口时使用。",
                                "若应用窗口已存在则优先激活现有窗口，并尽量最大化。",
                                "若应用未运行则先解析路径再启动，并尽量最大化主窗口。"
                            },
                            new[]
                            {
                                "打开或激活 微信",
                                "打开或激活 360浏览器"
                            },
                            new[] { "appName" },
                            new[] { "workingDirectory", "timeoutMs", "pollMs", "lane" })
                }
            };
        }

        private static JObject BuildBrowserCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "browser",
                ["enabled"] = true,
                ["description"] = compact ? null : "浏览器语义命令。用于指定浏览器激活、打开网址、在新标签页打开或执行网页搜索。",
                ["operationSelectionHint"] = "切换浏览器用 open_or_activate；已知网址用 open_url 或 open_url_in_tab；已知关键词用 search_web 或 search_site；这些浏览器命令默认尽量最大化窗口；需要指定浏览器时传 browserName。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "open_or_activate",
                            "打开或激活指定浏览器；browserName 必填；默认尽量最大化窗口。",
                            new[] { "browserName" },
                            new[] { "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_or_activate",
                            "打开或激活指定浏览器。",
                            new[]
                            {
                                "如果浏览器已经在运行，优先激活现有窗口，并尽量最大化。",
                                "适用于后续还要继续在该浏览器中操作的场景。"
                            },
                            new[]
                            {
                                "打开或激活 360浏览器"
                            },
                            new[] { "browserName" },
                            new[] { "timeoutMs", "pollMs", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "open_url",
                            "用指定浏览器打开 url；browserName 为空时退回系统默认浏览器；默认尽量最大化窗口。",
                            new[] { "url" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_url",
                            "用指定浏览器打开明确网址。",
                            new[]
                            {
                                "browserName 可空；为空时使用系统默认浏览器。",
                                "打开后默认尽量最大化浏览器窗口。",
                                "若已知必须使用某个浏览器，例如 360浏览器，应显式传 browserName。"
                            },
                            new[]
                            {
                                "用 360浏览器 打开 https://www.bilibili.com"
                            },
                            new[] { "url" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "open_url_in_tab",
                            "用指定浏览器在新标签页倾向打开 url；browserName 为空时退回默认浏览器；默认尽量最大化窗口。",
                            new[] { "url" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_url_in_tab",
                            "用指定浏览器在新标签页倾向打开明确网址。",
                            new[]
                            {
                                "底层会尽量复用已运行浏览器实例；多数浏览器会在现有实例中新开标签页。",
                                "打开后默认尽量最大化浏览器窗口。",
                                "若 browserName 为空，则退回系统默认浏览器处理。"
                            },
                            new[]
                            {
                                "用 Chrome 在新标签页打开 https://www.openai.com"
                            },
                            new[] { "url" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "search_web",
                            "用指定浏览器搜索 query；可选 site 限定站点；默认尽量最大化窗口。",
                            new[] { "query" },
                            new[] { "browserName", "site", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "search_web",
                            "用指定浏览器搜索网页关键词。",
                            new[]
                            {
                                "适用于“用某浏览器搜索某关键词”这类常见任务。",
                                "搜索结果窗口默认尽量最大化。",
                                "site 可填域名或站点名，用于缩小搜索范围。"
                            },
                            new[]
                            {
                                "用 Chrome 搜索 美女视频",
                                "用 360浏览器 搜索 site:bilibili.com 美女视频"
                            },
                            new[] { "query" },
                            new[] { "browserName", "site", "timeoutMs", "pollMs", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "search_site",
                            "用指定浏览器搜索 query，并要求 site 必填；默认尽量最大化窗口。",
                            new[] { "query", "site" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "search_site",
                            "用指定浏览器做站内搜索。",
                            new[]
                            {
                                "与 search_web 不同，search_site 要求 site 必填，适合强约束到特定站点。",
                                "搜索结果窗口默认尽量最大化。",
                                "site 可填域名或站点名。"
                            },
                            new[]
                            {
                                "用 360浏览器 搜索 bilibili 美女视频"
                            },
                            new[] { "query", "site" },
                            new[] { "browserName", "timeoutMs", "pollMs", "lane" })
                }
            };
        }

        private static JObject BuildClipboardCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "clipboard",
                ["enabled"] = true,
                ["description"] = compact ? null : "剪贴板命令。用于稳定复制文本或激活目标窗口后粘贴文本。",
                ["operationSelectionHint"] = "只想准备文本用 set_text；已知目标窗口且要稳定输入长文本时优先 paste_text。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "set_text",
                            "把 text 写入剪贴板。",
                            new[] { "text" },
                            new[] { "lane" })
                        : BuildOperation(
                            "set_text",
                            "把指定文本写入系统剪贴板。",
                            new[]
                            {
                                "适用于长文本、口令、网址等不适合逐字输入的内容。"
                            },
                            new[]
                            {
                                "把一段长文本放入剪贴板"
                            },
                            new[] { "text" },
                            new[] { "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "paste_text",
                            "把 text 放入剪贴板后执行粘贴；已知窗口时优先传 targetWindowTitleContains。",
                            new[] { "text" },
                            new[] { "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "paste_text",
                            "把指定文本放入剪贴板并执行粘贴。",
                            new[]
                            {
                                "适用于输入法不稳定、长文本、验证码旁路输入等场景。",
                                "已知目标窗口时建议同时传 targetWindowTitleContains。"
                            },
                            new[]
                            {
                                "向标题包含 微信 的窗口粘贴一段文本"
                            },
                            new[] { "text" },
                            new[] { "targetWindowTitleContains", "timeoutMs", "pollMs", "lane" })
                }
            };
        }

        private static JObject BuildVerifyCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "verify",
                ["enabled"] = true,
                ["description"] = compact ? null : "结果验收命令。用于确认窗口、文本和聊天消息是否真的达成。",
                ["operationSelectionHint"] = "确认窗口是否出现用 window_ready；确认一般界面文本可见用 text_visible；仅当要确认消息已从输入框进入聊天记录时才用 chat_message_sent；不要把一般文本可见映射成 chat_message_sent。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "window_ready",
                            "确认目标窗口已出现且可见。",
                            new[] { "targetWindowTitleContains" },
                            new[] { "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" })
                        : BuildOperation(
                            "window_ready",
                            "确认目标窗口已出现且可见。",
                            new[]
                            {
                                "targetWindowTitleContains 填目标窗口标题关键字。",
                                "适用于程序启动后、切换流程前的显式窗口验收。"
                            },
                            new[]
                            {
                                "确认记事本窗口已出现",
                                "确认微信窗口已在前台"
                            },
                            new[] { "targetWindowTitleContains" },
                            new[] { "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "text_visible",
                            "确认目标文本已经在界面中可见；仅表示文本可见，不表示消息已发送。",
                            new[] { "expectedTextContains" },
                            new[] { "targetWindowTitleContains", "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" })
                        : BuildOperation(
                            "text_visible",
                            "确认目标文本已经在界面中可见。",
                            new[]
                            {
                                "expectedTextContains 填要确认的文本内容。",
                                "targetWindowTitleContains 可选；已知窗口时建议同时填写，缩小误判范围。",
                                "适用于主界面文案、按钮文案、页面关键词等一般文本可见场景，不适用于确认聊天消息已发送。"
                            },
                            new[]
                            {
                                "确认界面中已显示 123456",
                                "确认浏览器页面已出现搜索关键词"
                            },
                            new[] { "expectedTextContains" },
                            new[] { "targetWindowTitleContains", "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "chat_message_sent",
                            "仅用于聊天场景，确认消息已出现在聊天记录中，而不是只停留在输入框；不要用于一般文本可见。",
                            new[] { "expectedTextContains", "targetWindowTitleContains" },
                            new[] { "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" })
                        : BuildOperation(
                            "chat_message_sent",
                            "确认聊天消息已经真正出现在聊天记录中，而不是只停留在输入框。",
                            new[]
                            {
                                "expectedTextContains 填消息文本，targetWindowTitleContains 填聊天窗口标题关键字。",
                                "内部会尽量忽略输入框类候选，降低“文本已输入但未发送”的误判。",
                                "仅当目标是确认聊天消息已经进入聊天记录时使用，不要替代一般文本可见验收。"
                            },
                            new[]
                            {
                                "确认文件传输助手中已发送 5555"
                            },
                            new[] { "expectedTextContains", "targetWindowTitleContains" },
                            new[] { "scope", "requireTargetVisible", "includeChildren", "maxChildren", "lane" })
                }
            };
        }

        private static JObject BuildExplorerCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "explorer",
                ["enabled"] = true,
                ["description"] = compact ? null : "资源管理器语义命令。用于打开路径、等待资源管理器到位或直接选中文件。",
                ["operationSelectionHint"] = "打开目录并等待用 open_path_and_wait；打开后直接定位并选中文件用 open_path_and_select；默认尽量最大化资源管理器窗口。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "open_path_and_wait",
                            "打开路径并等待资源管理器窗口到位；默认尽量最大化资源管理器窗口。",
                            new[] { "commandLine" },
                            new[] { "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_path_and_wait",
                            "打开路径并等待资源管理器窗口到位。",
                            new[]
                            {
                                "commandLine 填现有文件、目录或 shell: 路径。",
                                "适用于进入目录、打开磁盘或拉起资源管理器后再继续后续操作。",
                                "打开后默认尽量最大化资源管理器窗口。"
                            },
                            new[]
                            {
                                "打开 D:\\DataAI 并等待资源管理器就绪"
                            },
                            new[] { "commandLine" },
                            new[] { "timeoutMs", "pollMs", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "open_path_and_select",
                            "打开目录后直接选中文件；目录路径时 fileName 必填；默认尽量最大化资源管理器窗口。",
                            new[] { "commandLine" },
                            new[] { "fileName", "timeoutMs", "pollMs", "lane" })
                        : BuildOperation(
                            "open_path_and_select",
                            "打开目录后直接选中文件或目录；默认尽量最大化资源管理器窗口。",
                            new[]
                            {
                                "如果 commandLine 本身就是文件路径，则直接按该路径选中。",
                                "如果 commandLine 是目录，则 fileName 填目录中的目标文件名。"
                            },
                            new[]
                            {
                                "打开 D:\\DataAI 并选中 report.txt"
                            },
                            new[] { "commandLine" },
                            new[] { "fileName", "timeoutMs", "pollMs", "lane" })
                }
            };
        }

        private static JObject BuildFileCommandType(bool compact)
        {
            return new JObject
            {
                ["name"] = "file",
                ["enabled"] = true,
                ["description"] = compact ? null : "文件系统语义命令。用于直接创建目录、创建文本文件、保存文本和重命名文件。",
                ["operationSelectionHint"] = "新建文件夹/目录用 create_directory；创建新文本文件用 create_text_file；覆盖或新建目标文件用 save_as；同目录重命名用 rename。",
                ["operations"] = new JArray
                {
                    compact
                        ? BuildCompactOperation(
                            "create_directory",
                            "创建目录；目录已存在时返回成功，不再走右键菜单新建。",
                            new[] { "commandLine" },
                            new[] { "lane" })
                        : BuildOperation(
                            "create_directory",
                            "创建目录。",
                            new[]
                            {
                                "commandLine 填目标目录完整路径。",
                                "适用于“新建文件夹”“创建目录”“mkdir”这类已知路径动作，不要退回到资源管理器右键菜单。"
                            },
                            new[]
                            {
                                "在 D:\\DataAI 下创建 New Folder"
                            },
                            new[] { "commandLine" },
                            new[] { "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "create_text_file",
                            "创建文本文件；目标已存在时会拒绝。",
                            new[] { "commandLine" },
                            new[] { "text", "lane" })
                        : BuildOperation(
                            "create_text_file",
                            "创建文本文件。",
                            new[]
                            {
                                "commandLine 填目标文件完整路径。",
                                "text 可选；为空时创建空文件。若目标已存在会拒绝，避免误覆盖。"
                            },
                            new[]
                            {
                                "在 D:\\DataAI 下创建 note.txt"
                            },
                            new[] { "commandLine" },
                            new[] { "text", "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "save_as",
                            "把 text 保存到目标文件，允许覆盖。",
                            new[] { "commandLine", "text" },
                            new[] { "lane" })
                        : BuildOperation(
                            "save_as",
                            "把文本保存到目标文件，允许覆盖。",
                            new[]
                            {
                                "commandLine 填目标文件完整路径，text 填要保存的内容。",
                                "适用于 agent 直接落盘，不再走保存对话框。"
                            },
                            new[]
                            {
                                "把一段分析结果保存到 D:\\DataAI\\summary.txt"
                            },
                            new[] { "commandLine", "text" },
                            new[] { "lane" }),
                    compact
                        ? BuildCompactOperation(
                            "rename",
                            "把 commandLine 指向的文件或目录重命名为 fileName。",
                            new[] { "commandLine", "fileName" },
                            new[] { "lane" })
                        : BuildOperation(
                            "rename",
                            "重命名文件或目录。",
                            new[]
                            {
                                "commandLine 填原路径，fileName 填新的名称。",
                                "当前只做同目录重命名，不做跨目录移动。"
                            },
                            new[]
                            {
                                "把 D:\\DataAI\\old.txt 重命名为 new.txt"
                            },
                            new[] { "commandLine", "fileName" },
                            new[] { "lane" })
                }
            };
        }

        private static JObject BuildOperation(
            string name,
            string description,
            IEnumerable<string> usageHints,
            IEnumerable<string> examples,
            IEnumerable<string> requiredArgs,
            IEnumerable<string> optionalArgs)
        {
            return new JObject
            {
                ["name"] = name,
                ["enabled"] = true,
                ["description"] = description,
                ["usageHints"] = new JArray((usageHints ?? Array.Empty<string>()).Select(x => x)),
                ["examples"] = new JArray((examples ?? Array.Empty<string>()).Select(x => x)),
                ["requiredArgs"] = new JArray((requiredArgs ?? Array.Empty<string>()).Select(x => x)),
                ["optionalArgs"] = new JArray((optionalArgs ?? Array.Empty<string>()).Select(x => x))
            };
        }

        private static JObject BuildCompactOperation(
            string name,
            string usageHint,
            IEnumerable<string> requiredArgs,
            IEnumerable<string> optionalArgs)
        {
            return new JObject
            {
                ["name"] = name,
                ["enabled"] = true,
                ["usageHints"] = new JArray(usageHint),
                ["requiredArgs"] = new JArray((requiredArgs ?? Array.Empty<string>()).Select(x => x)),
                ["optionalArgs"] = new JArray((optionalArgs ?? Array.Empty<string>()).Select(x => x))
            };
        }

        private static JObject BuildMinimalOperation(
            string name,
            IEnumerable<string> requiredArgs)
        {
            return new JObject
            {
                ["name"] = name,
                ["requiredArgs"] = new JArray((requiredArgs ?? Array.Empty<string>()).Select(x => x))
            };
        }

        private static JObject SlimCompactCommandType(JObject commandType)
        {
            if (commandType == null)
            {
                return null;
            }

            commandType.Remove("enabled");
            commandType.Remove("description");
            commandType.Remove("operationSelectionHint");

            if (commandType["operations"] is JArray operations)
            {
                foreach (var operation in operations.OfType<JObject>())
                {
                    operation.Remove("enabled");
                    operation.Remove("description");
                    operation.Remove("examples");
                    operation.Remove("optionalArgs");
                }
            }

            return commandType;
        }
    }
}

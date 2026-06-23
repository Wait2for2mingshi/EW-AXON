using EW_Assistant.Services;
using EW_Assistant.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EW_Assistant.Views
{
    public partial class AIAssistantView : UserControl, INotifyPropertyChanged
    {
        private static readonly TimeSpan ChatHardTimeout = TimeSpan.FromMinutes(5);
        private const string DefaultImagePrompt = "请分析这张图片。";
        // 全局可用的接收器实例（程序启动时就创建，不随页面切换而销毁）
        public static AIAssistantView GlobalInstance { get; internal set; }

        private static string L(string chineseText)
        {
            return UiLanguageService.CurrentText(chineseText);
        }

        // ====== 对外数据源（绑定）======
        public ObservableCollection<MessageItem> Messages { get; } = new();
        // === 新增：Dify配置（可从外部注入/绑定）===

            public string ChatUserId { get; set; } = string.Empty; // 保证唯一即可

            private DifyChatAdapter _dify;


        private string _inputText;
        public string InputText
        {
            get => _inputText;
            set { _inputText = value; OnPropertyChanged(nameof(InputText)); }
        }

        private ImageAttachment _selectedImageAttachment;
        private ImageSource _selectedImagePreview;

        public bool HasSelectedImageAttachment => _selectedImageAttachment != null;

        public ImageSource SelectedImagePreview
        {
            get => _selectedImagePreview;
            private set { _selectedImagePreview = value; OnPropertyChanged(nameof(SelectedImagePreview)); }
        }

        public string SelectedImageFileName => _selectedImageAttachment?.FileName ?? string.Empty;

        public string SelectedImageSizeText => _selectedImageAttachment == null
            ? string.Empty
            : FormatFileSize(_selectedImageAttachment.FileSize);

        public enum StepStatus { Pending, Running, Done, Error }

        private sealed class ImageAttachment
        {
            public string FilePath { get; set; }
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public ImageSource Preview { get; set; }
        }


        private sealed class StreamState
        {
            // 后台线程只往队列丢 token；UI 线程独享 UiBuilder
            public readonly ConcurrentQueue<string> Tokens = new ConcurrentQueue<string>();
            public string ReplaceSnapshot; // 若有整段替换，这里暂存（原子交换）
            public readonly StringBuilder UiBuilder = new StringBuilder(1024); // 仅 UI 线程访问
            public UiCoalescer Flusher;
        }


        private readonly Dictionary<MessageItem, StreamState> _streamStates = new Dictionary<MessageItem, StreamState>();
        private UiCoalescer _scrollCoalescer;
        private ScrollViewer _chatScrollViewer;

        public class ProgressItem : INotifyPropertyChanged
        {
            private StepStatus _status;
            private string _durationText;

            public string Title { get; set; }
            public StepStatus Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
            public DateTime? StartAt { get; set; }
            public string DurationText { get => _durationText; set { _durationText = value; OnPropertyChanged(nameof(DurationText)); } }

            public void MarkRunning()
            {
                Status = StepStatus.Running;
                StartAt = DateTime.Now;
                DurationText = string.Empty;
            }

            public void MarkDone(double? elapsedSeconds = null)
            {
                Status = StepStatus.Done;
                if (elapsedSeconds.HasValue)
                    DurationText = $" ({elapsedSeconds.Value:0.######}s)";
                else if (StartAt.HasValue)
                    DurationText = $" ({(DateTime.Now - StartAt.Value).TotalSeconds:0.######}s)";
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
        public ObservableCollection<ProgressItem> ProgressItems { get; } = new();
        private string _currentRunningTitle;
        public string CurrentRunningTitle
        {
            get => _currentRunningTitle;
            set { _currentRunningTitle = value; OnPropertyChanged(nameof(CurrentRunningTitle)); }
        }

        private bool _isProgressVisible;
        public bool IsProgressVisible
        {
            get => _isProgressVisible;
            set { _isProgressVisible = value; OnPropertyChanged(nameof(IsProgressVisible)); }
        }

        // 复用 HttpClient，避免套接字耗尽
        private static readonly HttpClient s_http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // ====== 流式对接：外部实现推流 ======
        // 发送消息时触发；外部在回调中使用 e.AppendToken(...) 逐步推送 token
        public event EventHandler<StreamRequestEventArgs> StreamRequested;

        private CancellationTokenSource _cts;
        private long _nextStreamRequestId;
        private long _activeStreamRequestId;




        public AIAssistantView()
        {
            InitializeComponent();
            DataContext = this;
            GlobalInstance = this;
            InitializeDifyAdapter();

            // 绑定外部流事件（把“外部实现推流”用 Dify 来实现）
            this.StreamRequested += OnStreamRequested_Dify;
            Loaded += (_, __) => ScrollToEnd(); // 首次显示时把视图滚到末尾

        }

        private void InitializeDifyAdapter()
        {
            var cfg = ConfigService.Current;
            ChatUserId = string.IsNullOrWhiteSpace(cfg?.User) ? Environment.MachineName : cfg.User.Trim();
            _dify = new DifyChatAdapter(s_http, (cfg?.URL ?? string.Empty) + "/chat-messages", cfg?.ChatKey, ChatUserId);
        }

        private void ChatList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = EnsureChatScrollViewer();
            if (sv == null) return;

            double factor = 1.5;
            double delta = -e.Delta * factor; // Delta>0 表示向上滚
            sv.ScrollToVerticalOffset(sv.VerticalOffset + delta);
            e.Handled = true;
        }

        private ScrollViewer EnsureChatScrollViewer()
        {
            if (_chatScrollViewer != null) return _chatScrollViewer;
            _chatScrollViewer = FindChild<ScrollViewer>(ChatList);
            return _chatScrollViewer;
        }

        private static T FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var sub = FindChild<T>(child);
                if (sub != null) return sub;
            }
            return null;
        }

        private async void OnStreamRequested_Dify(object sender, StreamRequestEventArgs e)
        {
            try
            {
                // 打开进度卡片
                ProgressReset();
                IEnumerable<object> files = null;
                var fileLogSummary = BuildFileLogSummary(e.ImageFileName, e.ImageFileSize);

                if (e.HasImageAttachment)
                {
                    AddOrUpdateStep(L("上传图片"), running: true, done: false);
                    var uploadFileId = await _dify.UploadImageAsync(e.ImageFilePath, e.CancellationToken);

                    if (IsActiveStreamRequest(e.RequestId))
                        AddOrUpdateStep(L("上传图片"), running: false, done: true);

                    files = new[] { DifyChatAdapter.BuildLocalImageFileObject(uploadFileId) };
                }

                await _dify.SendStreamingAsync(
                    query: e.Input,
                    ct: e.CancellationToken,
                    onToken: e.AppendToken,
                    onReplaceAll: e.ReplaceAll,
                    onComplete: e.Complete,
                    inputs: new Dictionary<string, object>(),
                    autoGenerateName: true,
                    files: files,
                    onEvent: (evt, obj) => HandleDifyEvent(e.RequestId, evt, obj),   // ⬅️ 关键：驱动可视化
                    fileLogSummary: fileLogSummary
                );
            }
            catch (TaskCanceledException)
            {
                if (TryHandleExpectedStreamStop(e))
                    return;
            }
            catch (OperationCanceledException)
            {
                if (TryHandleExpectedStreamStop(e))
                    return;
            }
            catch (Exception ex)
            {
                if (TryHandleExpectedStreamStop(e))
                    return;

                e.AppendToken("\n\n> " + L("调用失败：") + ex.Message + "\n");
                e.Complete?.Invoke();
                if (IsActiveStreamRequest(e.RequestId))
                {
                    AddOrUpdateStep(L("错误"), running: false, done: false, asError: true);
                    CurrentRunningTitle = string.Empty;
                    ProgressDoneAndHideSoon(e.RequestId);
                }
            }
            finally
            {
                e.Dispose();
            }
        }

        private readonly Dictionary<string, ProgressItem> _progressIndex = new();

        private void ProgressReset()
        {
            ProgressItems.Clear();
            _progressIndex.Clear();
            CurrentRunningTitle = string.Empty;
            IsProgressVisible = true;
        }

        private void ProgressDoneAndHideSoon(long requestId)
        {
            // 1～1.5s 后隐藏（避免“闪一下”）
            var _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                Dispatcher.Invoke(() =>
                {
                    if (!IsActiveStreamRequest(requestId))
                        return;

                    IsProgressVisible = false;
                });
            });
        }

        private bool TryHandleExpectedStreamStop(StreamRequestEventArgs e)
        {
            if (e == null)
                return false;

            if (e.TimeoutCancellationToken.IsCancellationRequested
                && !e.UserCancellationToken.IsCancellationRequested)
            {
                e.AppendToken("\n\n> " + string.Format(L("对话超时，已在 {0} 分钟后自动停止。"), ChatHardTimeout.TotalMinutes.ToString("0")) + "\n");
                e.Complete?.Invoke();
                if (IsActiveStreamRequest(e.RequestId))
                {
                    AddOrUpdateStep(L("超时"), running: false, done: false, asError: true);
                    CurrentRunningTitle = string.Empty;
                    ProgressDoneAndHideSoon(e.RequestId);
                }

                _ = _dify?.TryStopAsync();
                return true;
            }

            if (e.UserCancellationToken.IsCancellationRequested || e.CancellationToken.IsCancellationRequested)
            {
                e.Complete?.Invoke();
                if (IsActiveStreamRequest(e.RequestId))
                {
                    CurrentRunningTitle = string.Empty;
                    ProgressDoneAndHideSoon(e.RequestId);
                }

                return true;
            }

            return false;
        }

        private bool IsActiveStreamRequest(long requestId)
        {
            return requestId == Interlocked.Read(ref _activeStreamRequestId);
        }

        private void HandleDifyEvent(long requestId, string evt, Newtonsoft.Json.Linq.JObject obj)
        {
            // 所有 UI 更新都走 UI 线程
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => HandleDifyEvent(requestId, evt, obj)); return; }
            if (!IsActiveStreamRequest(requestId)) return;

            switch (evt)
            {
                case "workflow_started":
                    ProgressReset();
                    // 可选：显示“开始”
                    AddOrUpdateStep(L("开始"), running: false, done: true, elapsed: 0.02);
                    break;

                case "node_started":
                    {
                        var title = (string)obj["data"]?["title"] ?? "Node";
                        CurrentRunningTitle = title;
                        AddOrUpdateStep(title, running: true, done: false);
                        break;
                    }

                case "node_finished":
                    {
                        var title = (string)obj["data"]?["title"] ?? "Node";
                        CurrentRunningTitle = string.Empty;

                        double? elapsed = null;
                        var elapsedToken = obj["data"]?["elapsed_time"];
                        if (elapsedToken != null && double.TryParse(elapsedToken.ToString(), out var sec))
                            elapsed = sec;

                        AddOrUpdateStep(title, running: false, done: true, elapsed: elapsed);
                        break;
                    }

                case "workflow_finished":
                    // 流程图走完，但 LLM 可能还在 message 流。
                    // 不马上隐藏，等 message_end 再隐藏更自然。
                    break;

                case "message_end":
                    // 回复结束，1.2s 后隐藏
                    ProgressDoneAndHideSoon(requestId);
                    break;

                case "error":
                    AddOrUpdateStep(L("错误"), running: false, done: false, elapsed: null, asError: true);
                    CurrentRunningTitle = string.Empty;
                    break;

                    // 其余：message/message_file/tts/ping...不用管
            }
        }

        private void AddOrUpdateStep(string title, bool running, bool done, double? elapsed = null, bool asError = false)
        {
            if (!_progressIndex.TryGetValue(title, out var item))
            {
                item = new ProgressItem { Title = title, Status = StepStatus.Pending };
                ProgressItems.Add(item);
                _progressIndex[title] = item;
            }

            if (asError)
            {
                item.Status = StepStatus.Error;
                return;
            }

            if (running)
            {
                item.MarkRunning();
                return;
            }

            if (done)
            {
                item.MarkDone(elapsed);
                return;
            }
        }

        // ====== 发送/停止（你已有） ======

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            // 额外通知 Dify 停止当前 task（尽量非阻塞）
            _ = _dify?.TryStopAsync();
        }

        private void ClearChatBtn_Click(object sender, RoutedEventArgs e)
        {
            ClearAll();
        }

        // ====== 发送与停止 ======
        private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendCurrentAsync();

        private void UploadImageBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = L("选择图片"),
                Filter = L("图片文件") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + L("所有文件") + "|*.*",
                Multiselect = false,
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                SetSelectedImageAttachment(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, L("图片不可用"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RemoveImageBtn_Click(object sender, RoutedEventArgs e)
        {
            ClearSelectedImageAttachment();
        }

        private Task SendCurrentAsync()
        {
            var text = (InputText ?? "").Trim();
            var imageAttachment = _selectedImageAttachment;
            if (string.IsNullOrEmpty(text) && imageAttachment == null)
                return Task.CompletedTask;

            if (imageAttachment != null)
            {
                try
                {
                    ValidateImageAttachmentForSend(imageAttachment);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, L("图片不可用"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    ClearSelectedImageAttachment();
                    return Task.CompletedTask;
                }

                if (string.IsNullOrEmpty(text))
                    text = DefaultImagePrompt;
            }
        
            // 在开启新一轮对话前执行限量清空
            MaybeAutoClearBeforeNewTurn();

            AddUserMarkdown(text, imageAttachment);
            InputText = string.Empty;
            ClearSelectedImageAttachment();

            StartStream(text, imageAttachment);
            return Task.CompletedTask;
        }
        private Task SendCurrentAsyncByInfo(string text)
        {
            // 在开启新一轮对话前执行限量清空
            MaybeAutoClearBeforeNewTurn();

            StartStream(text);
            return Task.CompletedTask;
        }

        private void StartStream(string text, ImageAttachment imageAttachment = null)
        {
            var bot = AddBotMarkdown(string.Empty);
            var requestId = Interlocked.Increment(ref _nextStreamRequestId);
            Interlocked.Exchange(ref _activeStreamRequestId, requestId);

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var timeoutCts = new CancellationTokenSource(ChatHardTimeout);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            var args = new StreamRequestEventArgs(
                requestId: requestId,
                input: text,
                ct: linkedCts.Token,
                userCt: _cts.Token,
                timeoutCt: timeoutCts.Token,
                appendToken: token => AppendAssistantToken(bot, token),
                replaceAll: md => ReplaceAssistantMarkdown(bot, md),
                complete: () => Dispatcher.BeginInvoke(new Action(() => CompleteAssistantMessage(bot)), DispatcherPriority.Background),
                linkedCts: linkedCts,
                timeoutCts: timeoutCts,
                imageFilePath: imageAttachment?.FilePath,
                imageFileName: imageAttachment?.FileName,
                imageFileSize: imageAttachment?.FileSize
            );

            if (StreamRequested == null)
            {
                AppendAssistantToken(bot, "\n\n> " + L("未找到流式处理器，无法发送当前请求。") + "\n");
                CompleteAssistantMessage(bot);
                return;
            }

            StreamRequested.Invoke(this, args);
        }
        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Shift+Enter 换行；Enter 发送
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                _ = SendCurrentAsync();
            }
        }
        public int HistoryLimit { get; set; } = 10;

        private void MaybeAutoClearBeforeNewTurn()
        {
            if (Messages.Count >= HistoryLimit)
            {
                ClearAll(); // 仅清空UI；如果你集成了 Dify，可在 ClearAll 里顺便重置会话（见下）
            }
        }

        // ====== 消息操作 ======
        public void AddUserMarkdown(string md)
        {
            AddUserMarkdown(md, null);
        }

        private void AddUserMarkdown(string md, ImageAttachment imageAttachment)
        {
            Messages.Add(new MessageItem
            {
                IsUser = true,
                Timestamp = DateTime.Now,
                Markdown = md,
                StreamText = null,
                IsFinal = true,        // ← 直接进入 Markdown 渲染
                ImagePreview = imageAttachment?.Preview,
                ImageFilePath = imageAttachment?.FilePath,
                ImageFileName = imageAttachment?.FileName,
                ImageFileSizeText = imageAttachment == null ? string.Empty : FormatFileSize(imageAttachment.FileSize)
            });
            ScrollToEnd();
        }


        /// <summary>新增机器人消息并返回引用（如需你在外部手动管理）。</summary>
        public MessageItem AddBotMarkdown(string md)
        {
            var msg = new MessageItem
            {
                IsUser = false,
                Timestamp = DateTime.Now,
                // 流式阶段显示轻量文本：把初始内容放入 StreamText；不填 Markdown
                StreamText = md,
                Markdown = null,
                IsFinal = false
            };
            Messages.Add(msg);

            var st = new StreamState();
            if (!string.IsNullOrEmpty(md)) st.UiBuilder.Append(md);

            st.Flusher = new UiCoalescer(Dispatcher, TimeSpan.FromMilliseconds(50), () =>
            {
                var snap = Interlocked.Exchange(ref st.ReplaceSnapshot, null);
                if (snap != null)
                {
                    st.UiBuilder.Length = 0;
                    st.UiBuilder.Append(snap);
                }
                string piece;
                while (st.Tokens.TryDequeue(out piece))
                    st.UiBuilder.Append(piece);

                // 流式阶段只更新轻量文本（TextBlock）
                msg.StreamText = DifyOutputSanitizer.StripReasoningBlocks(st.UiBuilder.ToString());
                ScrollToEndThrottled();
            });

            _streamStates[msg] = st;
            ScrollToEndThrottled();
            return msg;
        }

        /// <summary>直接添加最终 Markdown（不走流式）。</summary>
        public void AddBotMarkdownFinal(string md)
        {
            var cleaned = DifyOutputSanitizer.Clean(md);
            Messages.Add(new MessageItem
            {
                IsUser = false,
                Timestamp = DateTime.Now,
                Markdown = cleaned,
                StreamText = null,
                IsFinal = true
            });
            ScrollToEnd();
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            // 导出当前机器人回复为 Markdown 文件
            var fe = sender as FrameworkElement;
            var msg = fe?.DataContext as MessageItem;
            if (msg == null) return;

            var content = msg.Markdown;
            if (!msg.IsFinal || string.IsNullOrWhiteSpace(content))
            {
                MessageBox.Show(L("当前回复尚未完成，或没有可导出的 Markdown 内容。"), L("提示"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = L("Markdown 文件") + "|*.md|" + L("所有文件") + "|*.*",
                DefaultExt = ".md",
                AddExtension = true,
                FileName = $"AI-Reply-{msg.Timestamp:yyyyMMdd-HHmmss}.md"
            };

            var result = dialog.ShowDialog();
            if (result != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, content, new UTF8Encoding(false));
                MessageBox.Show(L("已导出当前回复。"), L("导出成功"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(L("导出失败：") + ex.Message, L("错误"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }




        public void AppendAssistantToken(MessageItem target, string token)
        {
            if (target == null) return;

            StreamState st;
            if (!_streamStates.TryGetValue(target, out st))
            {
                // 兜底：若没建流态，退回到 UI 线程直接附加（不建议，但保证不崩）
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => AppendAssistantToken(target, token)), DispatcherPriority.Background);
                    return;
                }
                target.Markdown = DifyOutputSanitizer.Clean((target.Markdown ?? string.Empty) + token);
                target.Touch();
                ScrollToEndThrottled();
                return;
            }

            st.Tokens.Enqueue(token);   // 后台线程只入队
            st.Flusher.Request();       // 触发 UI 合并器（50ms 内最多刷一次）
        }



        /// <summary>直接替换整条机器人消息 markdown（如服务端有段落级合并）。</summary>
        public void ReplaceAssistantMarkdown(MessageItem target, string markdown)
        {
            if (target == null) return;

            StreamState st;
            if (!_streamStates.TryGetValue(target, out st))
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => ReplaceAssistantMarkdown(target, markdown)), DispatcherPriority.Background);
                    return;
                }
                target.Markdown = DifyOutputSanitizer.Clean(markdown);
                target.Touch();
                ScrollToEndThrottled();
                return;
            }

            // 原子设置“整段替换”快照，交由 UI 线程一次性覆盖
            Interlocked.Exchange(ref st.ReplaceSnapshot, DifyOutputSanitizer.Clean(markdown));
            st.Flusher.Request();
        }

        private void CompleteAssistantMessage(MessageItem target)
        {
            if (target == null) return;

            StreamState st;
            if (_streamStates.TryGetValue(target, out st))
            {
                st.Flusher.Dispose();

                var snap = Interlocked.Exchange(ref st.ReplaceSnapshot, null);
                if (snap != null)
                {
                    st.UiBuilder.Length = 0;
                    st.UiBuilder.Append(snap);
                }
                string piece;
                while (st.Tokens.TryDequeue(out piece))
                    st.UiBuilder.Append(piece);

                // 关键：完成时才给 Markdown，并切换 IsFinal
                var finalText = DifyOutputSanitizer.Clean(st.UiBuilder.ToString());
                target.Markdown = finalText;
                target.StreamText = null;   // 可置空以节省内存
                target.IsFinal = true;      // 触发模板切换到 MdXaml

                _streamStates.Remove(target);
            }

            ScrollToEnd();
        }


        public void ClearAll()
        {
            StopAllBackgroundWork();
            ProgressItems.Clear();
            _progressIndex.Clear();
            CurrentRunningTitle = string.Empty;
            IsProgressVisible = false;
            Messages.Clear();
            InputText = string.Empty;
            ClearSelectedImageAttachment();
            InitializeDifyAdapter();
        }

        public void StopAllBackgroundWork()
        {
            try { _cts?.Cancel(); } catch { }
            _ = _dify?.TryStopAsync();
            _cts = null;
            Interlocked.Exchange(ref _activeStreamRequestId, 0);

            foreach (var kv in _streamStates.Values)
            {
                try { if (kv.Flusher != null) kv.Flusher.Dispose(); } catch { }
            }
            _streamStates.Clear();
        }

        private void SetSelectedImageAttachment(string filePath)
        {
            var attachment = CreateImageAttachment(filePath);
            _selectedImageAttachment = attachment;
            SelectedImagePreview = attachment.Preview;
            NotifySelectedImageAttachmentChanged();
        }

        private void ClearSelectedImageAttachment()
        {
            _selectedImageAttachment = null;
            SelectedImagePreview = null;
            NotifySelectedImageAttachmentChanged();
        }

        private void NotifySelectedImageAttachmentChanged()
        {
            OnPropertyChanged(nameof(HasSelectedImageAttachment));
            OnPropertyChanged(nameof(SelectedImageFileName));
            OnPropertyChanged(nameof(SelectedImageSizeText));
        }

        private static ImageAttachment CreateImageAttachment(string filePath)
        {
            DifyChatAdapter.ValidateImageFileForUpload(filePath);
            var info = new FileInfo(filePath);

            return new ImageAttachment
            {
                FilePath = info.FullName,
                FileName = info.Name,
                FileSize = info.Length,
                Preview = LoadImagePreview(info.FullName)
            };
        }

        private static void ValidateImageAttachmentForSend(ImageAttachment attachment)
        {
            if (attachment == null)
                return;

            DifyChatAdapter.ValidateImageFileForUpload(attachment.FilePath);
            var info = new FileInfo(attachment.FilePath);
            attachment.FileName = info.Name;
            attachment.FileSize = info.Length;
        }

        private static ImageSource LoadImagePreview(string filePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 360;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private static ImageSource LoadOriginalImage(string filePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }

        private void MessageImagePreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2)
                return;

            e.Handled = true;
            var msg = (sender as FrameworkElement)?.DataContext as MessageItem;
            if (msg == null || !msg.HasImageAttachment)
                return;

            OpenImagePreviewWindow(msg);
        }

        private void OpenImagePreviewWindow(MessageItem msg)
        {
            ImageSource imageSource = null;
            if (!string.IsNullOrWhiteSpace(msg.ImageFilePath) && File.Exists(msg.ImageFilePath))
            {
                try
                {
                    imageSource = LoadOriginalImage(msg.ImageFilePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L("图片文件无法打开：") + ex.Message, L("图片预览"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            imageSource = imageSource ?? msg.ImagePreview;
            if (imageSource == null)
                return;

            var scale = new ScaleTransform(1, 1);
            var image = new Image
            {
                Source = imageSource,
                Stretch = Stretch.None,
                LayoutTransform = scale,
                SnapsToDevicePixels = true,
                Cursor = Cursors.Hand
            };

            var scroll = new ScrollViewer
            {
                Content = image,
                Background = Brushes.Black,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var title = string.IsNullOrWhiteSpace(msg.ImageFileName) ? L("图片预览") : msg.ImageFileName;
            var window = new Window
            {
                Title = title,
                Width = 980,
                Height = 720,
                MinWidth = 520,
                MinHeight = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brushes.Black
            };
            var owner = Window.GetWindow(this);
            if (owner != null)
                window.Owner = owner;

            void SetScale(double value)
            {
                var next = Math.Max(0.1, Math.Min(8.0, value));
                scale.ScaleX = next;
                scale.ScaleY = next;
            }

            void FitToWindow()
            {
                if (!(imageSource is BitmapSource bitmap))
                {
                    SetScale(1);
                    return;
                }

                var viewportWidth = Math.Max(1, scroll.ViewportWidth - 24);
                var viewportHeight = Math.Max(1, scroll.ViewportHeight - 24);
                var fit = Math.Min(viewportWidth / bitmap.PixelWidth, viewportHeight / bitmap.PixelHeight);
                SetScale(double.IsInfinity(fit) || double.IsNaN(fit) ? 1 : Math.Min(1, fit));
            }

            window.Content = scroll;

            scroll.PreviewMouseWheel += (_, e) =>
            {
                var oldScale = scale.ScaleX;
                var factor = e.Delta > 0 ? 1.12 : 0.88;
                var mouseOnImage = e.GetPosition(image);
                var mouseOnViewport = e.GetPosition(scroll);

                SetScale(oldScale * factor);
                var newScale = scale.ScaleX;
                scroll.UpdateLayout();
                scroll.ScrollToHorizontalOffset(mouseOnImage.X * newScale - mouseOnViewport.X);
                scroll.ScrollToVerticalOffset(mouseOnImage.Y * newScale - mouseOnViewport.Y);
                e.Handled = true;
            };

            var isDragging = false;
            var lastDragPoint = new Point();

            scroll.PreviewMouseLeftButtonDown += (_, e) =>
            {
                isDragging = true;
                lastDragPoint = e.GetPosition(scroll);
                scroll.CaptureMouse();
                image.Cursor = Cursors.SizeAll;
                e.Handled = true;
            };

            scroll.PreviewMouseMove += (_, e) =>
            {
                if (!isDragging || e.LeftButton != MouseButtonState.Pressed)
                    return;

                var current = e.GetPosition(scroll);
                var delta = current - lastDragPoint;
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - delta.X);
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - delta.Y);
                lastDragPoint = current;
                e.Handled = true;
            };

            scroll.PreviewMouseLeftButtonUp += (_, e) =>
            {
                if (!isDragging)
                    return;

                isDragging = false;
                scroll.ReleaseMouseCapture();
                image.Cursor = Cursors.Hand;
                e.Handled = true;
            };

            scroll.LostMouseCapture += (_, __) =>
            {
                isDragging = false;
                image.Cursor = Cursors.Hand;
            };

            window.Loaded += (_, __) => window.Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.Background);
            window.ShowDialog();
        }

        private static string FormatFileSize(long size)
        {
            const double kb = 1024d;
            const double mb = kb * 1024d;

            if (size >= mb)
                return (size / mb).ToString("0.##") + " MB";
            if (size >= kb)
                return (size / kb).ToString("0.#") + " KB";

            return size + " B";
        }

        private static string BuildFileLogSummary(string fileName, long? fileSize)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            return fileSize.HasValue
                ? string.Format(L("图片：{0}（{1}）"), fileName, FormatFileSize(fileSize.Value))
                : string.Format(L("图片：{0}"), fileName);
        }




        // ====== INotifyPropertyChanged ======
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));


        private void QuickReportBtn_Click(object sender, RoutedEventArgs e)
        {
            var date = DateTime.Today.ToString("yyyy-MM-dd"); // 建议用采集地时区
            var sb = new StringBuilder();

            sb.AppendLine($"生成 **今天（{{DATE}}）0–24 点** 的客户版 **产量/良率/稼动率** 日报。只输出 **Markdown**；禁止输出代码块、JSON、工具调用日志或多余说明。");
            sb.AppendLine();
            sb.AppendLine("【这是单独一台设备数据】");
            sb.AppendLine("【必须按此取数】");
            sb.AppendLine("1) 仅调用 MCP 工具：GetHourlyStats(date=\"{DATE}\", startHour=0, endHour=24) 获取逐小时数据（hour, pass, fail, total, yield）。");
            sb.AppendLine("2) 在工具返回的基础上自行计算并用于报告：");
            sb.AppendLine("   - day_pass = Σpass；day_fail = Σfail；day_total = day_pass + day_fail；");
            sb.AppendLine("   - day_yield = day_pass / day_total（百分比，保留2位）；");
            sb.AppendLine("   - active_hours = 统计 total > 0 的小时数；");
            sb.AppendLine("   - active_rate = active_hours / 24（百分比，保留2位，作为“近似稼动率”）；");
            sb.AppendLine("   - 峰值小时 = 非零 total 的 Top-1~3 小时（含产量数）；");
            sb.AppendLine("   - 低谷小时 = 非零 total 的 Bottom-1~3 小时（含产量数）；");
            sb.AppendLine("   - 最长连续停机窗口 = 将 total=0 的相邻小时合并，取持续时间最长的一段（格式示例：“03:00–07:00，4小时”）；");
            sb.AppendLine("   - 波动指数 CV = 标准差(total) / 均值(total)（百分比，保留2位）；");
            sb.AppendLine();
            sb.AppendLine("【排版模板（章节顺序固定、全部必须出现）】");
            sb.AppendLine("# 今日产能日报（{DATE}）");
            sb.AppendLine();
            sb.AppendLine("## 概览指标");
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| PASS（良品） | {day_pass 千分位} |");
            sb.AppendLine("| FAIL（不良） | {day_fail 千分位} |");
            sb.AppendLine("| 总产量 | {day_total 千分位} |");
            sb.AppendLine("| **良率** | **{day_yield 两位百分比}** |");
            sb.AppendLine("| **活跃小时** | **{active_hours} 小时** |");
            sb.AppendLine("| **近似稼动率** | **{active_rate 两位百分比}** |");
            sb.AppendLine("| 峰值小时 | {例如 “10:00–11:00（1,024件）” 等 1–3 个} |");
            sb.AppendLine("| 最长停机窗口 | {如无则写“无”} |");
            sb.AppendLine("| 波动指数（CV） | {两位百分比} |");
            sb.AppendLine();
            sb.AppendLine("> 注：近似稼动率=有产出小时数/24，用于日常管理直观参考。");
            sb.AppendLine();
            sb.AppendLine("## 逐小时明细");
            sb.AppendLine("> 0–23 点必须全覆盖；若某小时工具未返回，按 PASS=0, FAIL=0, Total=0, Yield=0% 填充。");
            sb.AppendLine();
            sb.AppendLine("| 时段 | PASS | FAIL | 总量 | 良率(%) | 备注 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---|");
            sb.AppendLine("（逐行渲染以下行模板，共24行）逐小时表格“必须严格按 hours 数组逐行渲染”，不得跳过 0 产出小时、不得合并区间；表格行数必须等于 hours 的长度（正常为 24 行）。");
            sb.AppendLine("| {HH}:00–{HH+1}:00 | {pass 千分位} | {fail 千分位} | {total 千分位} | {yield 两位} | {重点观察，如停机窗口/低良率/异常缺口；无则留空} |");
            sb.AppendLine();
            sb.AppendLine("**备注填写原则：**");
            sb.AppendLine("- 仅在该小时存在停机、低良率、明显峰谷或数据缺口时做简要说明；");
            sb.AppendLine("- 无明显异常时可留空，避免机械套用固定标签。");
            sb.AppendLine();
            sb.AppendLine("**合计行（表末尾追加一行）：**");
            sb.AppendLine("| **合计** | **ΣPASS** | **ΣFAIL** | **ΣTotal** | **{day_yield 两位}%** | **活跃 {active_hours}/24** |");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## 产能与良率诊断");
            sb.AppendLine("- 基于峰值/低谷/停机窗口/低良率时段，给出 **5–8 条**业务可读的洞见与改善建议（例如：排班与交接、点检/快速换线、治具维护、程序/工艺参数优化、备料节拍、在制/WIP 管控等）。");
            sb.AppendLine("- 语气客观，避免开发术语。");
            sb.AppendLine();
            sb.AppendLine("## 注意事项");
            sb.AppendLine("- 如存在缺失 CSV、解析异常或跨天数据空洞，请逐条列出；若无则写“无”。");
            sb.AppendLine();
            sb.AppendLine("【格式与风格要求】");
            sb.AppendLine("- 所有数值使用 **千分位**；百分比 **保留2位**；时间统一 `HH:00`。");
            sb.AppendLine("- 中文商务表述，避免“一句话结论”、避免开发字段名。");
            sb.AppendLine("- **必须**渲染完整 24 行小时表与“合计行”；不可省略章节。");
            sb.AppendLine();
            sb.AppendLine("【失败兜底】");
            sb.AppendLine("- 如工具报错或无数据，仍按本模板输出各章节：表格填 0；并在“注意事项”中写明原因。");
            sb.AppendLine();
            sb.AppendLine("【可选增强】");
            sb.AppendLine("- 若存在报警工具（GetHourlyAlarms / QueryAlarms），追加一节：");
            sb.AppendLine("  ## 报警关联（概览）");
            sb.AppendLine("  - 按小时统计报警条数，标注与“停机/低谷”重合的时段，列出示例原因；若无该工具则跳过本节。");

            var prompt = sb.ToString().Replace("{DATE}", date);
            try { MainWindow.PostProgramInfo(L("AI生成报表中，请稍候"), "info"); } catch { }
            SendCurrentAsyncByInfo(prompt);
        }

        private void QuickAlarmReportBtn_Click(object sender, RoutedEventArgs e)
        {
            var date = DateTime.Today.ToString("yyyy-MM-dd"); // 建议用采集地时区
            var sb = new StringBuilder();

            sb.AppendLine($"生成 **今天（{{DATE}}）0–24 点** 的 **报警日报**。只输出 **Markdown**；禁止输出代码块、JSON、工具调用日志或多余说明。");
            sb.AppendLine();
            sb.AppendLine("【这是单独一台设备数据】");
            sb.AppendLine("【必须按此取数】");
            sb.AppendLine("1) 仅允许调用 1 次 MCP 工具（禁止调用其他任何工具）：");
            sb.AppendLine("   - GetHourlyProdWithAlarms(date=\"{DATE}\", startHour=0, endHour=24)");
            sb.AppendLine();
            sb.AppendLine("2) 你只能使用该工具返回的 items 字段：");
            sb.AppendLine("   - hour, pass, fail, total, yield, alarmCount, alarmDurationSec, topAlarmCode, topAlarmSeconds, topAlarmContent");
            sb.AppendLine();
            sb.AppendLine("3) 你需要自行计算：");
            sb.AppendLine("   - avg_s = alarmDurationSec / max(1, alarmCount)；");
            sb.AppendLine("   - day_count = Σ alarmCount；day_seconds = Σ alarmDurationSec；avg_per_alarm = day_seconds / max(1, day_count)；");
            sb.AppendLine("   - active_hours = alarmDurationSec > 0 的小时数；");
            sb.AppendLine("   - 峰值小时：alarmDurationSec 最大的小时（若全为0写“无”）；");
            sb.AppendLine();
            sb.AppendLine("【空值/缺省规则（必须遵守）】");
            sb.AppendLine("- 若 alarmCount=0 或 alarmDurationSec=0：Top代码=“无”，Top内容=“无”。");
            sb.AppendLine("- 若 topAlarmCode 为空：Top代码=“无”。");
            sb.AppendLine("- 若 topAlarmContent 为空：Top内容=“无”。（不要写“内容缺失”）");
            sb.AppendLine();
            sb.AppendLine("【排版模板（顺序固定、全部必须出现）】");
            sb.AppendLine("# 今日报警日报（{DATE}）");
            sb.AppendLine();
            sb.AppendLine("## 概览指标");
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|:---|---:|");
            sb.AppendLine("| 报警条数 | {day_count 千分位} |");
            sb.AppendLine("| 报警总时长 | {day_seconds 转 xhym 或 ms} |");
            sb.AppendLine("| 平均单次(秒) | {avg_per_alarm 一位} |");
            sb.AppendLine("| 活跃小时 | {active_hours}/24 |");
            sb.AppendLine("| 峰值小时 | {若存在：HH:00–HH+1:00（xhym/条数）；否则“无”} |");
            sb.AppendLine();

            sb.AppendLine("## 逐小时明细");
            sb.AppendLine("> 0–23 点必须全覆盖；缺数据的小时按 count=0, seconds=0, avg_s=0 填充。");
            sb.AppendLine();
            sb.AppendLine("| 时段 | 条数 | 时长 | 平均(s) | Top代码 | Top内容 | 备注 |");
            sb.AppendLine("|:---|---:|---:|---:|:---|:---|:---|");
            sb.AppendLine("（严格渲染 24 行；不得跳过 0 值小时，不得合并区间；必须 7 列，不得增减列）");
            sb.AppendLine("| {HH}:00–{HH+1}:00 | {count 千分位} | {seconds 时分秒} | {avg_s 一位} | {topCode} | {topContent} | {重点观察，如无报警/高发时段/长时报警线索；无则留空} |");
            sb.AppendLine();
            sb.AppendLine("**备注填写原则：**");
            sb.AppendLine("- 仅在该小时存在无报警、报警集中、单次时长偏长等明显现象时简要说明；");
            sb.AppendLine("- 无明显异常时可留空，避免机械套用固定标签。");
            sb.AppendLine();
            sb.AppendLine("**合计行（表末尾追加）：**");
            sb.AppendLine("| **合计** | **Σcount** | **Σseconds** | **{avg_per_alarm 一位}** |  |  | **活跃 {active_hours}/24** |");
            sb.AppendLine();

            sb.AppendLine("## 今日 Top 报警（近似统计：按各小时 Top1 聚合）");
            sb.AppendLine("- 仅基于每小时 topAlarmCode/topAlarmSeconds 做近似聚合，输出 Top5（按 ΣtopAlarmSeconds 降序）：");
            sb.AppendLine("  - 代码 | 内容 | ΣtopAlarmSeconds(转时分秒) | 出现小时数");
            sb.AppendLine();

            sb.AppendLine("## 处理措施建议（5–8 条，务实可执行）");
            sb.AppendLine("- 每条按：**关联代码/内容** → **动作** → **验证指标**（例如报警总时长下降、某工位成功率提升）。");
            sb.AppendLine();

            sb.AppendLine("## 异常与注意事项");
            sb.AppendLine("- 若日报出现大量 Top内容=无：说明“报警文件无内容或内容为空，当前仅能展示代码”；否则写“无”。");
            sb.AppendLine();
            sb.AppendLine("【格式要求】");
            sb.AppendLine("- 数值千分位；百分比两位；时长用 `xhym` 或 `ms`；时间统一 `HH:00`。");
            sb.AppendLine("- 严禁编造：所有数值必须来自工具返回或由其严格计算；逐小时表 24 行不可缺。");

            var prompt = sb.ToString().Replace("{DATE}", date);

            try { MainWindow.PostProgramInfo(L("AI生成报警报表中，请稍候"), "info"); } catch { }
            SendCurrentAsyncByInfo(prompt);
        }




        private void QuickWeeklyReportBtn_Click(object sender, RoutedEventArgs e)
        {
            var endDt = DateTime.Today;
            var startDt = endDt.AddDays(-6);
            var endDate = endDt.ToString("yyyy-MM-dd");
            var startDate = startDt.ToString("yyyy-MM-dd");
            var range = $"{startDate} ~ {endDate}";

            var sb = new StringBuilder();

            sb.AppendLine($"请输出 **{range}（最近7天）** 的产能周报，必须使用 Markdown，禁止 JSON、原始日志或随意发挥。");
            sb.AppendLine();
            sb.AppendLine("请按以下步骤执行：");
            sb.AppendLine($"1) 仅调用 MCP 工具 GetWeeklyProductionSummary(endDate=\"{endDate}\")，取得 `summary`（pass/fail/total/yield/avgYield/medianTotal/volatility/lastDay/lastDayDelta/bestDays/worstDays）、`days`（逐日明细）以及 `warnings`。");
            sb.AppendLine("2) 所有周度 KPI 必须直接引用 summary；日度表格数据来自 days，不得自行演算或猜测缺失字段。");
            sb.AppendLine("3) 若 warnings 不为空或某天缺 CSV，须在“异常/缺失”章节逐条说明，并在日度表中标注原因。");
            sb.AppendLine("4) 基于 bestDays / worstDays / lastDayDelta 给出有洞见的亮点、薄弱点与改进建议，尽量结合具体日期与数值。");
            sb.AppendLine();
            sb.AppendLine("输出模板（章节与表头不可删改，可补充文字说明）：");
            sb.AppendLine($"# 产能周报（{range}）");
            sb.AppendLine();
            sb.AppendLine("## 周度KPI");
            sb.AppendLine("| 指标 | 数值 | 说明 |");
            sb.AppendLine("|---|---:|---|");
            sb.AppendLine("| PASS 总量 | {summary.pass 千分位} | 一周内通过品数量总和 |");
            sb.AppendLine("| FAIL 总量 | {summary.fail 千分位} | 一周内不良品数量总和 |");
            sb.AppendLine("| 总产量 | {summary.total 千分位} | 一周内实际测试的总数量（PASS + FAIL） |");
            sb.AppendLine("| 周整体良率 | {summary.yield 百分比2位} | 以一周总产量为基准的整体通过率 |");
            sb.AppendLine("| 周均良率 | {summary.avgYield 百分比2位} | 7 天良率的平均水平，代表本周“常态”表现 |");
            sb.AppendLine("| 中位产量 | {summary.medianTotal 千分位} | 7 天日产量从小到大排序后的中间值，代表典型日产量 |");
            sb.AppendLine("| 产量波动 | {summary.volatility 百分比2位} | 反映每天产量波动程度，数值越大波动越明显（CV） |");
            sb.AppendLine("| 最后1天 vs 周均 | {summary.lastDayDelta.total 百分比1位}/{summary.lastDayDelta.yield 百分比1位} | 最后一天产量/良率相对本周平均的增减（正值=高于周均） |");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## 日度表现");
            sb.AppendLine("| 日期 | PASS | FAIL | 总量 | 良率 | 备注 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---|");
            sb.AppendLine("按 days 的顺序逐日渲染，备注列写明 warnings 对应的缺失、异常或特别说明；正常日期可写“正常”或留空。");
            sb.AppendLine();
            sb.AppendLine("## 重点洞察");
            sb.AppendLine("- 亮点：结合 summary.bestDays，说明哪些日期/班次产能和良率表现突出，以及可能原因。");
            sb.AppendLine("- 薄弱：结合 summary.worstDays，分析拖累周度指标的薄弱日期/工况，对周均的影响有多大。");
            sb.AppendLine("- 趋势：描述 7 天产能与良率走势、明显峰谷，以及 summary.lastDay / lastDayDelta 反映的最新状态。");
            sb.AppendLine();
            sb.AppendLine("## 风险与改进");
            sb.AppendLine("- 给出 2–3 条可执行措施，明确对应的验证指标（例如目标良率、目标日产量）和预计改善幅度。");
            sb.AppendLine();
            sb.AppendLine("## 异常/缺失");
            sb.AppendLine("- 若存在工具报错或缺 CSV，逐条列出日期与原因；否则写“无”。");
            sb.AppendLine();
            sb.AppendLine("> 所有指标必须来自 GetWeeklyProductionSummary 的结果，严禁输出 JSON 或凭空编造数据。");

            var prompt = sb.ToString();
            try { MainWindow.PostProgramInfo(L("AI产能周报提示注入完成"), "info"); } catch { }
            SendCurrentAsyncByInfo(prompt);
        }


        private void QuickWeeklyAlarmBtn_Click(object sender, RoutedEventArgs e)
        {
            var endDate = TodayStr();
            var startDate = DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd");
            var range = $"{startDate} ~ {endDate}";
            var sb = new StringBuilder();

            sb.AppendLine($"请输出 **{range}（最近7天）** 的报警周报，仅允许 Markdown 表达，禁止 JSON / 原始日志 / 代码块里的 JSON。");
            sb.AppendLine();
            sb.AppendLine("数据准备：");
            sb.AppendLine($"1) 调用 `ProdAlarmTools.GetAlarmImpactSummary(startDate=\"{startDate}\", endDate=\"{endDate}\", window=\"\")`，得到：");
            sb.AppendLine("   - `weeklyTotals.alarmSeconds`：周度去重后的报警总时长（秒，按小时并集 ≤24*3600*7，用于衡量设备真实挂报警时间）；");
            sb.AppendLine("   - `weeklyTotals.activeHours`：有报警秒数的小时数（覆盖小时数）；");
            sb.AppendLine("   - `weeklyTotals.lowYieldRowCount`：低良率小时条数（= lowYield.rows.Count）；");
            sb.AppendLine("   - `correlation.alarmSeconds_vs_yield`：报警秒数 vs 良率 的皮尔逊相关系数；");
            sb.AppendLine("   - `byDay`：每日聚合（`date / pass / fail / total / yield / alarmSeconds / alarmCount`）；");
            sb.AppendLine("   - `lowYield.rows`：低良率小时明细（含 `date / hour / total / yield / alarmSeconds / alarmCount / topAlarmCode / topAlarmSeconds`）；");
            sb.AppendLine("   - `lowYield.topAlarmCodes`：在低良率时段中累计时长靠前的报警代码。");
            sb.AppendLine();
            sb.AppendLine($"2) 调用 `AlarmCsvTools.GetAlarmRangeWindowSummary(startDate=\"{startDate}\", endDate=\"{endDate}\", window=\"\", topN=10, sortBy=\"duration\")`，得到：");
            sb.AppendLine("   - `totals.count`：报警记录条数（统计口径：CSV 明细行数）；");
            sb.AppendLine("   - `totals.durationSeconds`：报警记录累计时长（可能大于实际时间，用于类别占比和 Top 排序）；");
            sb.AppendLine("   - `byCategory`：各类别的次数 / 时长及占比；");
            sb.AppendLine("   - `top`：Top 报警代码列表（含 `code / content / count / duration`）。");
            sb.AppendLine();
            //sb.AppendLine("3) 如需引用具体样本，可按需调用：");
            //sb.AppendLine("   `AlarmCsvTools.QueryAlarms(startDate, endDate, code=某个 top.code, keyword=\"\", window=\"\", take=3)`，");
            //sb.AppendLine("   并从返回的 `items` 中抽取典型案例，写明开始时间 / 代码 / 描述 / 持续时间 / 来源文件。");
            sb.AppendLine();
            sb.AppendLine("3) 所有数值和结论必须基于上述工具返回的数据进行计算和归纳，不得凭空猜测。");
            sb.AppendLine();
            sb.AppendLine("输出模板：");
            sb.AppendLine($"# 报警周报（{range}）");
            sb.AppendLine();
            sb.AppendLine("## 周度KPI");
            sb.AppendLine("| 指标 | 数值 | 说明 |");
            sb.AppendLine("|---|---:|---|");
            sb.AppendLine("| 报警次数 | {alarm_record_count 千分位} | 一周内记录到的报警总次数 |");
            sb.AppendLine("| 报警总时长 | {impact_seconds 转 xhym} | 一周内设备处于报警状态的累计时间 |");
            sb.AppendLine("| 平均单次时长 | {avg_duration 秒1位} | 每次报警平均持续多长时间，建议写成“约 X 分钟/次” |");
            sb.AppendLine("| 覆盖小时数 | {active_hours}/168 | 本周有报警发生的小时数，占一周 168 小时的多少 |");
            sb.AppendLine("| 低良率小时 | {low_yield_hours 千分位} | 本周良率低于设定阈值（如 95%）的小时数 |");
            sb.AppendLine("| 报警-良率相关 | {pearson 百分比2位} | 报警时间与良率高低的关联程度（数值越接近 100% 关联越强） |");
            sb.AppendLine();
            sb.AppendLine("> 表格中的“说明”面向现场/管理人员，请用通俗中文描述，不要出现接口名或内部字段名。");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("## 日度趋势");
            sb.AppendLine("| 日期 | 报警次数 | 报警时长 | 良率 | Top 报警（概括） |");
            sb.AppendLine("|---|---:|---:|---:|---|");
            sb.AppendLine("- 按日期升序遍历 `GetAlarmImpactSummary.byDay`，列出 `date / alarmCount / alarmSeconds / yield`。");
            sb.AppendLine("- 对每一天，可结合 `GetAlarmRangeWindowSummary.top` 和必要时的 `QueryAlarms`，用 1–2 句自然语言概括当日最典型或影响最大的报警现象（不强制精确到绝对 Top 代码）。");
            sb.AppendLine();
            sb.AppendLine("## 低良率与 Top 报警");
            sb.AppendLine("- 从 `lowYield.rows` 中筛选关键小时，按 `日期 + HH:00-HH+1:00 / yield / alarmSeconds / alarmCount / topAlarmCode` 展开，解释这些小时良率下降的主要原因。");
            sb.AppendLine("- 结合 `lowYield.topAlarmCodes` 与 `AlarmRangeWindowSummary.top`，分析前三大报警在：");
            sb.AppendLine("  - 低良率小时中的累计时长和次数；");
            sb.AppendLine("  - 对整体产能 / 良率的影响（例如：贡献了多少比例的低良率小时报警时长）。");
            sb.AppendLine();
            sb.AppendLine("## 样本与措施");
            sb.AppendLine("- 至少给出 2–3 条典型报警样本：包含开始时间、报警代码、报警内容、持续时间（换算成 min）、涉及工序 / 工位（如能识别）、以及对应的产能 / 良率影响。");
            sb.AppendLine("- 针对 Top 报警，总结本周已采取或计划采取的措施，例如：参数优化、软件修正、治具维护、培训等，并给出预计改善方向或关闭时间。");
            sb.AppendLine();
            sb.AppendLine("## 异常 / 缺失");
            sb.AppendLine("- 将 `GetAlarmImpactSummary.warnings` 中的内容逐条整理，例如：缺失某天产能 / 报警文件。");
            sb.AppendLine("- 若本周数据完整且无特别异常，请明确写出“无”。");
            sb.AppendLine();
            sb.AppendLine("> 严格使用 Markdown 输出整份周报，禁止输出任何 JSON 结构或原始 CSV 行。");

            var prompt = sb.ToString();
            try { MainWindow.PostProgramInfo(L("AI报警周报提示注入完成"), "info"); } catch { }
            SendCurrentAsyncByInfo(prompt);


        }

        // 低良率扫描（当天，阈值可调） —— 升级版
        private void BtnLowYield_Click(object sender, RoutedEventArgs e)
        {
            var d = TodayStr();        // 若你没有 TodayStr()，可改为 DateTime.Today.ToString("yyyy-MM-dd")
            var threshold = 95;        // 可绑定到 UI
            var sb = new StringBuilder();

            sb.AppendLine($"生成 **{d}** 的【低良率小时扫描（阈值 {threshold}%）】报告。");
            sb.AppendLine("只输出 **Markdown**；禁止代码块、JSON、工具调用日志或多余说明。");
            sb.AppendLine();
            sb.AppendLine("【必须按此取数】");
            sb.AppendLine($"仅调用：GetTopAlarmsDuringLowYield(startDate=\"{d}\", endDate=\"{d}\", threshold={threshold}, window=\"\")。");
            sb.AppendLine("使用工具返回 rows（命中小时）与 top（聚合代码）；严禁编造。");
            sb.AppendLine();
            sb.AppendLine("【派生计算】");
            sb.AppendLine("- avg_s = alarmSeconds / max(1, alarmCount)（秒，保留1位）。");
            sb.AppendLine("- 重点现象由 rows 中的实际数值归纳，例如报警集中、单次时长偏长或停机重叠，不要套固定标签。");
            sb.AppendLine();
            sb.AppendLine($"# 低良率小时扫描（{d}，阈值 {threshold}%）");
            sb.AppendLine();
            sb.AppendLine("## 概览指标");
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine("| 命中小时数 | {rows.Count 千分位} |");
            sb.AppendLine("| 覆盖比例 | {rows.Count/24 两位百分比} |");
            sb.AppendLine("| 最低良率时段 | {示例 “10:00–11:00（良率xx%/产量x,xxx）”} |");
            sb.AppendLine("| 命中小时平均报警单次 | {均值(avg_s) 一位}s |");
            sb.AppendLine();
            sb.AppendLine("## 逐小时明细（按良率升序，最多 24 行）");
            sb.AppendLine("| 时段 | 产量 | 良率(%) | 报警时长 | 报警条数 | 平均单次(s) | 重点现象 |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---|");
            sb.AppendLine("（rows 中逐行渲染；时段格式 `HH:00–HH+1:00`；数值千分位、百分比两位；avg_s 一位；重点现象基于实际数值归纳）");
            sb.AppendLine();
            sb.AppendLine("## 分类 Top（低良率窗口内）");
            sb.AppendLine("- 取 top（按 seconds 降序，最多 10 条），格式：`代码 — 总时长/条数`。");
            sb.AppendLine("- 每条补“典型机理（≤12字）+ 快速排查（≤16字）”，结合 rows 的文本线索，避免空话。");
            sb.AppendLine();
            sb.AppendLine("## 行动建议（L1/操作 & L2/工程）");
            sb.AppendLine("输出 **5–8 条**闭环措施，按性价比排序：");
            sb.AppendLine("| 关联代码/现象 | 具体措施 | ETA | 验证指标 | 预期收益 |");
            sb.AppendLine("|---|---|---|---|---|"); // ← 修复：5列对应5个分隔符
            sb.AppendLine("- 示例：清洁/紧固/复位、相机/治具点检、传感器/接插件检查、阈值复核、快速旁路策略。");
            sb.AppendLine("- 示例：去抖与阈值优化、模板/对位参数重训、冗余/稳压/屏蔽、异常分级与重试、日志与监测点新增。");
            sb.AppendLine();
            sb.AppendLine("## 次日复核项");
            sb.AppendLine("- 以量化口径列 3–5 条：如“Top代码报警时长↓30%”、“对位成功率≥99.5%”、“回零成功率≥99.9%”、“通讯重试率≤0.5%”。");
            sb.AppendLine();
            sb.AppendLine("## 异常与注意事项");
            sb.AppendLine("- 如工具报错/无数据/跨天空洞，请逐条说明；否则写“无”。");
            sb.AppendLine();
            sb.AppendLine("【格式要求】");
            sb.AppendLine("- 全文中文商务表述；**数值千分位、百分比两位**；时间统一 `HH:00`；时长 `xhym` 或 `ms`。");
            sb.AppendLine("- 仅 Markdown 文本与表格；**严禁**代码块与 JSON。");

            var prompt = sb.ToString();
            try { MainWindow.PostProgramInfo(L("扫描低良率窗口…"), "info"); } catch { }
            SendCurrentAsyncByInfo(prompt);
        }

        // 统一日期（可改为你界面上的日期选择器值）
        private static string TodayStr()
        {
            return DateTime.Today.ToString("yyyy-MM-dd");
        }

        private bool IsNearBottom()
        {
            var sv = FindDescendant<ScrollViewer>(ChatList);
            if (sv == null) return true;
            return sv.VerticalOffset >= sv.ScrollableHeight - 30; // 距底 30px 内才认为需要吸底
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject { for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T t) return t; var deeper = FindDescendant<T>(child); if (deeper != null) return deeper; } return null; }

        private void ScrollToEndThrottled()
        {
            if (_scrollCoalescer == null)
            {
                _scrollCoalescer = new UiCoalescer(Dispatcher, TimeSpan.FromMilliseconds(80), () =>
                {
                    if (Messages.Count > 0 && IsNearBottom())
                        ChatList.ScrollIntoView(Messages.Last());
                });
            }
            _scrollCoalescer.Request();
        }

        private void ScrollToEnd()
        {
            if (Messages.Count > 0 && IsNearBottom())
                ChatList.ScrollIntoView(Messages.Last());
        }



    }

    // ====== 事件参数：外部对接推流 ======
    public class StreamRequestEventArgs : EventArgs, IDisposable
    {
        public long RequestId { get; }
        public string Input { get; }
        public CancellationToken CancellationToken { get; }
        public CancellationToken UserCancellationToken { get; }
        public CancellationToken TimeoutCancellationToken { get; }
        public Action<string> AppendToken { get; }
        public Action<string> ReplaceAll { get; }
        public Action Complete { get; }
        public string ImageFilePath { get; }
        public string ImageFileName { get; }
        public long? ImageFileSize { get; }
        public bool HasImageAttachment => !string.IsNullOrWhiteSpace(ImageFilePath);
        private readonly CancellationTokenSource _linkedCts;
        private readonly CancellationTokenSource _timeoutCts;

        public StreamRequestEventArgs(long requestId, string input, CancellationToken ct, CancellationToken userCt, CancellationToken timeoutCt,
            Action<string> appendToken, Action<string> replaceAll, Action complete,
            CancellationTokenSource linkedCts, CancellationTokenSource timeoutCts,
            string imageFilePath = null, string imageFileName = null, long? imageFileSize = null)
        {
            RequestId = requestId;
            Input = input;
            CancellationToken = ct;
            UserCancellationToken = userCt;
            TimeoutCancellationToken = timeoutCt;
            AppendToken = appendToken;
            ReplaceAll = replaceAll;
            Complete = complete;
            _linkedCts = linkedCts;
            _timeoutCts = timeoutCts;
            ImageFilePath = imageFilePath;
            ImageFileName = imageFileName;
            ImageFileSize = imageFileSize;
        }

        public void Dispose()
        {
            try { _linkedCts?.Dispose(); } catch { }
            try { _timeoutCts?.Dispose(); } catch { }
        }
    }

    public class MessageItem : INotifyPropertyChanged
    {
        private string _markdown;
        private string _streamText;     // ← 新增：流式阶段展示的轻量文本
        private bool _isFinal;          // ← 新增：是否切换为最终 Markdown 渲染
        private ImageSource _imagePreview;
        private string _imageFilePath;
        private string _imageFileName;
        private string _imageFileSizeText;

        public bool IsUser { get; set; }
        public DateTime Timestamp { get; set; }
        public bool HasImageAttachment => ImagePreview != null;

        public string Markdown
        {
            get { return _markdown; }
            set { _markdown = value; OnPropertyChanged(nameof(Markdown)); }
        }

        public string StreamText
        {
            get { return _streamText; }
            set { _streamText = value; OnPropertyChanged(nameof(StreamText)); }
        }

        public bool IsFinal
        {
            get { return _isFinal; }
            set { _isFinal = value; OnPropertyChanged(nameof(IsFinal)); }
        }

        public ImageSource ImagePreview
        {
            get { return _imagePreview; }
            set
            {
                _imagePreview = value;
                OnPropertyChanged(nameof(ImagePreview));
                OnPropertyChanged(nameof(HasImageAttachment));
            }
        }

        public string ImageFilePath
        {
            get { return _imageFilePath; }
            set { _imageFilePath = value; OnPropertyChanged(nameof(ImageFilePath)); }
        }

        public string ImageFileName
        {
            get { return _imageFileName; }
            set { _imageFileName = value; OnPropertyChanged(nameof(ImageFileName)); }
        }

        public string ImageFileSizeText
        {
            get { return _imageFileSizeText; }
            set { _imageFileSizeText = value; OnPropertyChanged(nameof(ImageFileSizeText)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void Touch() { OnPropertyChanged(nameof(Markdown)); }
        protected void OnPropertyChanged(string name)
        {
            var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }


}

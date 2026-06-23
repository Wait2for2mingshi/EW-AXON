using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Threading.Tasks;

namespace EW_Assistant.ViewModels
{
    /// <summary>
    /// 提供基本属性变更通知的基类。
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 简单的命令实现，支持异步委托。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Func<object, Task> executeAsync, Func<object, bool> canExecute = null)
        {
            if (executeAsync == null)
            {
                throw new ArgumentNullException("executeAsync");
            }

            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public async void Execute(object parameter)
        {
            try
            {
                await _executeAsync(parameter);
            }
            catch (Exception ex)
            {
                // 这里简单吞掉异常，实际可替换为日志或 UI 提示
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged()
        {
            var handler = CanExecuteChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}

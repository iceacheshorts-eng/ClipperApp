using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace ClipStudio.Services
{
    public interface IActivityLogger
    {
        void Log(string message);
        ObservableCollection<string> Logs { get; }
    }

    public class ActivityLogger : IActivityLogger
    {
        public ObservableCollection<string> Logs { get; } = new ObservableCollection<string>();
        private readonly Dispatcher _dispatcher;

        public ActivityLogger()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void Log(string message)
        {
            var timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _dispatcher.InvokeAsync(() =>
            {
                Logs.Add(timestampedMessage);
            });
        }
    }
}

using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace MorphLab.Sprinkler.Revit
{
    /// <summary>
    /// The dockable panel lives outside Revit's API context, so every button queues its work here
    /// and Revit runs it on its own thread (same pattern as the MCP RevitBridge).
    /// Execute() runs on Revit's UI thread, which is also the panel's thread, so the callback can
    /// update WPF controls directly.
    /// </summary>
    public class RevitTask : IExternalEventHandler
    {
        static RevitTask _handler;
        static ExternalEvent _event;
        readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();

        public static Action<Exception> OnError;

        /// <summary>Call once from OnStartup (an API context).</summary>
        public static void Init()
        {
            _handler = new RevitTask();
            _event = ExternalEvent.Create(_handler);
        }

        public static void Run(Action<UIApplication> work)
        {
            if (_event == null) return;
            _handler._queue.Enqueue(work);
            _event.Raise();
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var work))
            {
                try { work(app); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* user pressed Esc */ }
                catch (Exception ex) { OnError?.Invoke(ex); }
            }
        }

        public string GetName() => "MorphLab Sprinkler";
    }
}

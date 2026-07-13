using System;
using System.Collections.Generic;
using NLog;
using SwSopAddin.Infrastructure;

namespace SwSopAddin.Orchestration
{
    /// <summary>
    /// V2 方案 §6.9 RollbackManager — RAII 模式。
    /// 用法:
    ///   using (var rb = new RollbackManager()) {
    ///       DoStep1();
    ///       rb.Track(() => UndoStep1());  // 失败时倒序调用这些 undo
    ///       DoStep2();
    ///       rb.Track(() => UndoStep2());
    ///       rb.Commit();  // 全部成功,不再回滚
    ///   }
    /// 异常时 Dispose 自动 Rollback。
    /// </summary>
    public class RollbackManager : IDisposable
    {
        private static readonly Logger Log = Logging.ForType(typeof(RollbackManager));

        private readonly Stack<Action> _undoActions = new Stack<Action>();
        private bool _committed;
        private bool _disposed;

        public void Track(Action undoAction)
        {
            if (_committed)
                throw new InvalidOperationException("已 Commit,不能再 Track 新的 undo。");
            if (_disposed)
                throw new InvalidOperationException("已 Dispose,不能再 Track 新的 undo。");
            if (undoAction == null) throw new ArgumentNullException(nameof(undoAction));
            _undoActions.Push(undoAction);
        }

        public void Commit()
        {
            if (_disposed)
                throw new InvalidOperationException("已 Dispose,不能再 Commit。");
            _committed = true;
            Log.Debug("RollbackManager Commit — {0} 个 undo 全部丢弃", _undoActions.Count);
        }

        public void Rollback()
        {
            Log.Info("Rollback 开始 — 倒序执行 {0} 个 undo", _undoActions.Count);
            int i = 0;
            while (_undoActions.Count > 0)
            {
                var act = _undoActions.Pop();
                i++;
                try
                {
                    act();
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Rollback 第 {0} 步失败,继续下一 undo", i);
                }
            }
            Log.Info("Rollback 完成 — 执行 {0} 个 undo", i);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed) Rollback();
        }
    }
}

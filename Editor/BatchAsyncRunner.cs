using System;
using System.Collections.Generic;

namespace GitPackageManager.Editor
{
    internal sealed class BatchAsyncRunner
    {
        internal sealed class BatchItem
        {
            public CommandSpec Spec;
            public Action<CommandResult> OnComplete;
        }

        private readonly List<BatchItem> items;
        private readonly int maxConcurrent;
        private readonly List<AsyncCommandHandle> activeHandles = new();
        private readonly List<int> activeIndices = new();
        private int nextIndex;

        internal bool IsComplete => CompletedCount >= TotalCount;
        internal int CompletedCount { get; private set; }
        internal int TotalCount => items.Count;

        internal BatchAsyncRunner(List<BatchItem> items, int maxConcurrent)
        {
            this.items = items;
            this.maxConcurrent = maxConcurrent;
        }

        internal bool Tick()
        {
            bool changed = false;

            for (int i = activeHandles.Count - 1; i >= 0; i--)
            {
                if (!activeHandles[i].IsComplete)
                {
                    continue;
                }

                int itemIndex = activeIndices[i];
                items[itemIndex].OnComplete?.Invoke(activeHandles[i].Result);
                CompletedCount++;
                activeHandles.RemoveAt(i);
                activeIndices.RemoveAt(i);
                changed = true;
            }

            while (activeHandles.Count < maxConcurrent && nextIndex < items.Count)
            {
                var item = items[nextIndex];
                var handle = new AsyncCommandHandle(CliCommandRunner.CurrentRunner, item.Spec);
                handle.Start();
                activeHandles.Add(handle);
                activeIndices.Add(nextIndex);
                nextIndex++;
            }

            return changed;
        }
    }
}

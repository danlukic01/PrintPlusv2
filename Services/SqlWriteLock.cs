using System;
using System.Threading;
using System.Threading.Tasks;

namespace PrintPlusService.Services
{
    public static class SqliteWriteLock
    {
        private static readonly SemaphoreSlim _writeLock = new(1, 1);

        public static async Task RunWithWriteLock(Func<Task> action)
        {
            await _writeLock.WaitAsync();
            try
            {
                await action();
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}

// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Interfaces
{
    public abstract class Disposable : IDisposable
    {
        #region Methods

        protected abstract void ResourceDispose();

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                ResourceDispose();
            }
        }

        ~Disposable()
        {
            Dispose(false);
        }

        #endregion

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
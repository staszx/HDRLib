// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib;

using System.Runtime.InteropServices;

/// <summary>
/// Represents a pinned array of unmanaged elements, providing a fixed pointer for native interop.
/// </summary>
public unsafe class PinnedArray<T> : IDisposable where T : unmanaged
{
    #region Fields

    private GCHandle handle;

    #endregion

    #region Constructors

    /// <summary>
/// Pins the specified array in memory.
/// </summary>
/// <param name="array">Array to pin. Must not be null.</param>
public PinnedArray(T[] array)
    {
        if (array == null)
        {
            throw new ArgumentNullException(nameof(array));
        }

        this.Length = array.Length;
        this.handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        this.Pointer = (T*)this.handle.AddrOfPinnedObject();
    }

    #endregion

    #region Properties

    /// <summary>
/// Gets a pointer to the first element of the pinned array.
/// </summary>
public T* Pointer { get; private set; }
    /// <summary>
/// Gets the number of elements in the array.
/// </summary>
public int Length { get; }

    #endregion

    /// <summary>
/// Releases the pinned handle and clears the pointer.
/// </summary>
public void Dispose()
    {
        if (this.handle.IsAllocated)
        {
            this.handle.Free();
            this.Pointer = null;
        }
    }
}
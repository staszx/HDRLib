// Copyright (c) Stanislav Popov. All rights reserved.

namespace HDRLib.Gpu;

using ILGPU;
using ILGPU.Runtime;

public class GpuContext : IDisposable
{
    #region Fields

    private readonly Context context;
    private readonly GpuProcessor processor;

    internal GpuProcessor Processor => this.processor;

    #endregion

    #region Constructors

    public GpuContext(int acceleratorNumber = -1)
    {
        this.context = Context.Create(builder =>
        {
            builder.AllAccelerators();
          //  builder.EnableAlgorithms();
        });
        var device = acceleratorNumber < 0 ? this.context.GetPreferredDevice(false) : this.context.Devices[acceleratorNumber];
        this.Accelerator = device.CreateAccelerator(this.context);
        this.processor = new GpuProcessor(this);
        Console.WriteLine(this.Accelerator);
    }

    #endregion

    #region Properties

    public Accelerator Accelerator { get; }

    #endregion

    #region Methods

    public static List<Device> GetAccelerators()
    {
        using (var context = Context.Create(builder => builder.AllAccelerators()))
        {
            return context.Devices.ToList();
        }
    }

    #endregion

    public void Dispose()
    {
        this.Accelerator?.Dispose();
        this.context?.Dispose();
    }
}
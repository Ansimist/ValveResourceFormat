using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer.UniformBuffers
{
    public enum ReservedBufferSlots
    {
        View = 0,
        Lighting = 1,
        EnvmapBinding = 3,
        InstanceBuffer = 4,
        TransformBuffer = 5,
    }

    class UniformBuffer<T> : Buffer
        where T : new()
    {
        T data;
        public T Data { get => data; set { data = value; Update(); } }

        // A buffer where the structure is marshalled into, before being sent to the GPU
        readonly float[] cpuBuffer;
        readonly GCHandle cpuBufferHandle;

        public UniformBuffer(int bindingPoint) : base(BufferTarget.UniformBuffer, bindingPoint, typeof(T).Name)
        {
            Size = Marshal.SizeOf<T>();
            Debug.Assert(Size % 16 == 0);

            cpuBuffer = new float[Size / 4];
            cpuBufferHandle = GCHandle.Alloc(cpuBuffer, GCHandleType.Pinned);

            data = new T();
            Initialize();
        }

        public UniformBuffer(ReservedBufferSlots slot) : this((int)slot) { }

        private void WriteToCpuBuffer()
        {
            Debug.Assert(Size == Marshal.SizeOf(data));
            Marshal.StructureToPtr(data, cpuBufferHandle.AddrOfPinnedObject(), false);
        }

        private void Initialize()
        {
            WriteToCpuBuffer();
            GL.NamedBufferData(Handle, Size, cpuBuffer, BufferUsageHint.StaticDraw);
            BindBufferBase();
        }

        public void Update()
        {
            WriteToCpuBuffer();
            GL.NamedBufferSubData(Handle, IntPtr.Zero, Size, cpuBuffer);
        }

        public override void Dispose()
        {
            cpuBufferHandle.Free();
            base.Dispose();
        }
    }
}

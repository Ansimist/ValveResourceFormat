using OpenTK.Graphics.OpenGL;

namespace GUI.Types.Renderer.UniformBuffers
{
    abstract class Buffer : IDisposable
    {
        public BufferTarget Target { get; }
        public int Handle { get; }
        public int BindingPoint { get; }
        public string Name { get; }

        public virtual int Size { get; set; }


        protected Buffer(BufferTarget target, int bindingPoint, string name)
        {
            Target = target;
            GL.CreateBuffers(1, out int Handle);
            BindingPoint = bindingPoint;
            Name = name;

#if DEBUG
            Bind();
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, Handle, Name.Length, Name);
            Unbind();
#endif
        }

        protected void Bind() => GL.BindBuffer(Target, Handle);
        protected void Unbind() => GL.BindBuffer(Target, 0);

        public void SetBlockBinding(Shader shader)
        {
            var blockIndex = shader.GetUniformBlockIndex(Name);
            if (blockIndex > -1)
            {
                GL.UniformBlockBinding(shader.Program, blockIndex, BindingPoint);
            }
        }

        public virtual void Dispose()
        {
            GL.DeleteBuffer(Handle);
        }
    }
}

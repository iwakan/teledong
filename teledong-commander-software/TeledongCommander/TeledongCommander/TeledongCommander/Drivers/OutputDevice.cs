using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander
{
    public abstract class OutputDevice
    {
        public virtual bool IsConnected { get; private set; } = false;
        public virtual bool HasError { get; private set; } = false;
        public virtual string StatusText { get; private set; } = "";

        public OutputProcessor Processor { get; protected set; }

        public event EventHandler? StatusChanged;

        virtual public Task Connect() => Task.CompletedTask;
        virtual public Task Disconnect() => Task.CompletedTask;
        virtual public void InputPostion(double position)
        {
            Processor.PutPositionAndProcessOutput(position);
        }
    }
}

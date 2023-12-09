using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander
{
    public abstract class OutputDevice
    {
        public virtual bool IsStarted { get; private set; } = false;
        public virtual bool HasError { get; private set; } = false;
        public virtual string StatusText { get; private set; } = "";

        public OutputProcessor Processor { get; protected set; }

        public OutputDevice() 
        {
            Processor = new OutputProcessor();
        }

        public event EventHandler? StatusChanged;

        virtual public Task Start() => Task.CompletedTask;
        virtual public Task Stop() => Task.CompletedTask;
        virtual public void InputPostion(double position)
        {
            if (!IsStarted)
                return;

            Processor.PutPositionAndProcessOutput(position);
        }

        protected void TriggerStatusChanged()
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

using Avalonia.Threading;
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
        public virtual bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public virtual string StatusText { get; private set; } = "";

        public string? ErrorMessage = null;

        public OutputProcessor Processor { get; protected set; }

        public event EventHandler? StatusChanged;

        public OutputDevice()
        {
            Processor = new OutputProcessor();
        }

        virtual public async Task Start() { }
        virtual public async Task Stop() { }
        virtual public void InputPostion(double position)
        {
            if (!IsStarted)
                return;

            Processor.PutPositionAndProcessOutput(position);
        }

        protected void TriggerStatusChanged()
        {
            // This depends on Avalonia even though the model shouldn't, but I don't know enough about MVVM to care to deal with this yet.
            Dispatcher.UIThread.Invoke(new Action(() =>
            {
                StatusChanged?.Invoke(this, EventArgs.Empty);
            }));
        }
    }
}

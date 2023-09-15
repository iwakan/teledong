using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeledongCommander.ViewModels
{
    public class ViewModelBase : ObservableObject//: ReactiveObject
    {
        /*public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }*/
    }
}
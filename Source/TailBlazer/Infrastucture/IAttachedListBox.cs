using System.Collections.Generic;
using System.Windows.Controls;
using DynamicData;
using TailBlazer.Views;

namespace TailBlazer.Infrastucture
{
    public interface IAttachedListBox
    {
        void Receive(ListBox selector);
    }

    public interface ISelectionMonitor
    {
        string GetSelectedText();

        IEnumerable<string> GetSelectedItems();

        IObservableList<LineProxy> Selected { get; }
    }
}
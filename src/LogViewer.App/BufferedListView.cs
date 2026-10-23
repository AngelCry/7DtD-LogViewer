namespace LogViewer.App;

/// <summary>ListView с двойной буферизацией для плавной отрисовки виртуальных элементов.</summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
    }
}

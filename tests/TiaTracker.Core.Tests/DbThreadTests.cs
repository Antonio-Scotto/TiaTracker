using TiaTracker.Data;

namespace TiaTracker.Core.Tests;

public class DbThreadTests
{
    [Fact]
    public void FuoriDalThreadDelDbEUnErroreInModoStretto()
    {
        using Db db = Db.OpenInMemory();
        Assert.True(db.Strict);

        Assert.Equal(Db.LatestSchemaVersion, db.SchemaVersion);
        Exception? error = OnOtherThread(() => db.Scalar("SELECT 1"));
        InvalidOperationException ex = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("thread", ex.Message);
    }

    [Fact]
    public void SenzaModoStrettoSiSegnalaESiProsegue()
    {
        using Db db = Db.OpenInMemory();
        db.Strict = false;
        List<string> seen = new();
        Action<string>? before = Db.OnWrongThread;
        Db.OnWrongThread = m =>
        {
            lock (seen)
            {
                seen.Add(m);
            }
        };
        try
        {
            object? one = null;
            Assert.Null(OnOtherThread(() => one = db.Scalar("SELECT 1")));
            Assert.Equal(1L, one);
            Assert.Single(seen);
        }
        finally
        {
            Db.OnWrongThread = before;
        }
    }

    /// <summary>Un thread vero: Task.Run aspettato puo' girare sul thread chiamante.</summary>
    private static Exception? OnOtherThread(Action action)
    {
        Exception? error = null;
        Thread t = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.Start();
        t.Join();
        return error;
    }

    [Fact]
    public void LeMigrazioniSiFermanoDoveSiChiede()
    {
        using Db db = Db.OpenInMemory(1);
        Assert.Equal(1, db.SchemaVersion);
        db.Migrate();
        Assert.Equal(Db.LatestSchemaVersion, db.SchemaVersion);
    }
}

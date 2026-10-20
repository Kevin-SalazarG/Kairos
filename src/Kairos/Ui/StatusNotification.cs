using System.ComponentModel;

namespace Kairos.Ui;

/// <summary>Compares a displayed value without boxing it and reuses its change event.</summary>
internal abstract class StatusNotification<TContext>(string name)
{
    protected readonly PropertyChangedEventArgs Changed = new(name);

    public abstract PropertyChangedEventArgs? ReadChanged(TContext context);
    public abstract void Invalidate();

    public static StatusNotification<TContext> Create<TValue>(string name, Func<TContext, TValue> read) =>
        new ValueNotification<TValue>(name, read);

    sealed class ValueNotification<TValue>(string name, Func<TContext, TValue> read) : StatusNotification<TContext>(name)
    {
        TValue _lastValue = default!;
        bool _seen;

        public override void Invalidate() => _seen = false;

        public override PropertyChangedEventArgs? ReadChanged(TContext context)
        {
            TValue value = read(context);
            if (_seen && EqualityComparer<TValue>.Default.Equals(value, _lastValue))
                return null;
            _lastValue = value;
            _seen = true;
            return Changed;
        }
    }
}

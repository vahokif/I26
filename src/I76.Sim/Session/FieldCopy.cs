using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace I76.Sim.Session;

/// <summary>
/// Shallow copies of the simulation's plain state objects (a wheel, the
/// engine, a weapon, a damage part): every instance field, so a field added
/// later is snapshotted without anyone remembering to. Read-only fields are
/// set once at construction and are left alone.
/// </summary>
public static class FieldCopy
{
    private static readonly Func<object, object> ShallowCopy = (Func<object, object>)Delegate.CreateDelegate(
        typeof(Func<object, object>), typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!);

    private static readonly ConcurrentDictionary<Type, Action<object, object>> Copiers = new();

    /// <summary>A shallow copy: value fields copied, references shared.</summary>
    public static T Clone<T>(T o) where T : class => (T)ShallowCopy(o);

    /// <summary>Every writable instance field of <paramref name="from"/> into <paramref name="to"/> (the same runtime type).</summary>
    public static void Into(object from, object to)
    {
        if (from.GetType() != to.GetType()) throw new ArgumentException($"{from.GetType()} into {to.GetType()}");
        Copiers.GetOrAdd(from.GetType(), Build)(from, to);
    }

    private static Action<object, object> Build(Type t)
    {
        var a = Expression.Parameter(typeof(object), "from");
        var b = Expression.Parameter(typeof(object), "to");
        var ta = Expression.Variable(t, "a");
        var tb = Expression.Variable(t, "b");
        var body = new List<Expression> { Expression.Assign(ta, Expression.Convert(a, t)), Expression.Assign(tb, Expression.Convert(b, t)) };
        for (var k = t; k is not null && k != typeof(object); k = k.BaseType)
            foreach (var f in k.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsInitOnly && !f.IsLiteral)
                    body.Add(Expression.Assign(Expression.Field(tb, f), Expression.Field(ta, f)));
        return Expression.Lambda<Action<object, object>>(Expression.Block([ta, tb], body), a, b).Compile();
    }
}

internal static class P10Assert
{
    internal static void True(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    internal static void Equal<T>(T expected, T actual, string reason)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{reason}:expected={expected}:actual={actual}");
    }

    internal static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string reason)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException(reason);
    }

    internal static TException Throws<TException>(Action action,
        string reason)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException(reason);
    }

    internal static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action,
        string reason)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException(reason);
    }
}

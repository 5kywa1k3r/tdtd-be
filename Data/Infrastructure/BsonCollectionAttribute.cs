namespace tdtd_be.Data.Infrastructure
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class BsonCollectionAttribute: Attribute
    {
        public string Name { get; }

        public BsonCollectionAttribute(string name)
        {
            Name = name;
        }
    }
}

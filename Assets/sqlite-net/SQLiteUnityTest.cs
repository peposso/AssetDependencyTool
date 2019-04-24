using UnityEngine;
// using UnityEditor;
// using UnityEngine.TestTools;
using NUnit.Framework;
using SQLite;

namespace SQLiteUnityTest
{

    public class Person
    {
        [PrimaryKey, AutoIncrement]
        public int ID { get; set; }
        public string Name { get; set; }
        [Indexed]
        public int Age { get; set; }
    }

    public class Tests
    {

        [Test]
        public void Test1()
        {
            // Use the Assert class to test conditions.
            var conn = new SQLiteConnectionString("test.db", false);
            var db = new SQLiteConnection(conn);
            db.Tracer = s => Debug.Log(s);
            db.Trace = false;
            db.DropTable<Person>();
            db.CreateTable<Person>();

            var person = new Person();
            for (var i = 0; i < 1000; ++i)
            {
                person.Name = new string(new[] { RandChar(), RandChar(), RandChar(), RandChar() });
                person.Age = Random.Range(10, 50);
                db.Insert(person);
            }
        }

        char RandChar()
        {
            return (char)Random.Range((int)'a', (int)'z');
        }
    }
}

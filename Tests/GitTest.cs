using NUnit.Framework;

namespace GitTest
{
    public class MyMathTests
    {
        [Test(Author = "Martin Calander", Description = "Git Submodule")]
        public void AddsTwoPositiveIntegers()
        {
            Assert.AreEqual(3, 3);
        }
    }
}

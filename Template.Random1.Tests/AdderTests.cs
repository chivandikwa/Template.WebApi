namespace Template.Random1.Tests
{
    using Xunit;
    using Random;

    public class AdderTests
    {
        [Fact]
        public void Adder_Simple()
        {
            var adder = new Adder();

            const int expected = 10;

            var result = adder.Add(2, 8);

            Assert.Equal(expected, result);
        }
    }
}

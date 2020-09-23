using Xunit;

namespace Template.Random2.Tests
{
    public class MultiplierTests
    {
        [Fact]
        public void Multiplier_Simple()
        {
            var multiplier = new Multiplier();

            const int expected = 16;

            var result = multiplier.Multiply(2, 8);

            Assert.Equal(expected, result);
        }
    }
}

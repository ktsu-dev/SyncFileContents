# ktsu.io.SignificanNumber

## 1.1.0

**New Features:**

- **Arithmetic Operations:**
  - Added `Squared()` method: Returns the square of the current significant number.
  - Added `Cubed()` method: Returns the cube of the current significant number.
  - Added `Pow(int power)` method: Returns the result of raising the current significant number to the specified power.

**Documentation:**

- **README.md:**
  - Updated installation instructions to use a version placeholder.
  - Added examples for the new `Squared`, `Cubed`, and `Pow` methods under the Arithmetic Operations section.

**Unit Tests:**

- Added unit tests to ensure the correctness of the new methods:
  - `Squared_ShouldReturnCorrectValue()`: Tests the `Squared` method.
  - `Cubed_ShouldReturnCorrectValue()`: Tests the `Cubed` method.
  - `Pow_ShouldReturnCorrectValue()`: Tests the `Pow` method with a positive power.
  - `Pow_ZeroPower_ShouldReturnOne()`: Tests the `Pow` method with zero as the power.
  - `Pow_NegativePower_ShouldReturnCorrectValue()`: Tests the `Pow` method with a negative power.

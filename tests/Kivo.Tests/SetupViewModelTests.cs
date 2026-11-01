using Kivo.Core.Validation;
using Kivo.Windows.ViewModels;
using Xunit;

namespace Kivo.Tests;

public sealed class SetupViewModelTests
{
    [Fact]
    public void Folder_validation_notifies_when_start_button_should_enable_or_disable()
    {
        var viewModel = new SetupViewModel(new TestValidator(), null);
        var validityChanges = new List<bool>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SetupViewModel.IsValid))
            {
                validityChanges.Add(viewModel.IsValid);
            }
        };

        Assert.False(viewModel.IsValid);
        viewModel.FolderPath = "valid";
        Assert.True(viewModel.IsValid);
        Assert.Equal("Folder looks good.", viewModel.Message);
        viewModel.FolderPath = "";
        Assert.False(viewModel.IsValid);
        Assert.Collection(validityChanges,
            value => Assert.True(value),
            value => Assert.False(value));
    }

    private sealed class TestValidator : IFolderValidator
    {
        public FolderValidationResult Validate(string? path) =>
            path == "valid" ? FolderValidationResult.Ok : FolderValidationResult.Fail("Choose a folder.");
    }
}

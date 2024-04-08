# What is Feature Availablity?
Feature Availablity is an API that can tell you a specific feature's availability status. Feature is saved as a string and availability is an enum `FeatureStatus`: `Undefined`, `Available`, `NotAvailable`, `Preview`.

# How to use?
## API
In `Microsoft.Build.Framework` use `FeatureStatus Features.CheckFeatureAvailability(string featureName)` to get the feature availability.

## Command line switch
Use `/featureavailability`(`-featureavailability`) or `/fa`()`-fa` switches.

## Property function `CheckFeatureAvailability`
Use `string CheckFeatureAvailability(string featureName)` property function.
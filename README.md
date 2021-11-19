# git-links-mapper

Map git links on work items in the target by tracking down the repo by name in the source.

```
# for example: git-links-mapper.exe <source org url> <source org pat> <target org url> <target org pat> <target project> <target area path>

git-links-mapper.exe https://dev.azure.com/your_source_org <source org pat> https://dev.azure.com/your_target_org <target org pat> "SomeProject123"
```

Note: Use double quotes around the last parameter (target project) in case your project name contains spaces.
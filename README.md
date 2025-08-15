# Vintage Story Play To Earn
Base template for creating a vintage story compatibility with PTE coin

### Features
- /balance -> view player balance to receive
- /wallet -> register player wallet

### Dependencies
- [AFKModule](https://mods.vintagestory.at/afkmodule)
- [PTE Database Server](https://github.com/Play-To-Earn-Currency/pte_databaseserver)

# Using
- Install a database like mysql or mariadb
- Create a user for the database: GRANT ALL PRIVILEGES ON pte_wallets.* TO 'pte_admin'@'localhost' IDENTIFIED BY 'supersecretpassword' WITH GRANT OPTION; FLUSH PRIVILEGES;
- Create a table named vintagestory:
```sql
CREATE TABLE vintagestory (
    uniqueid VARCHAR(255) NOT NULL PRIMARY KEY,
    walletaddress VARCHAR(255) DEFAULT null,
    value DECIMAL(50, 0) NOT NULL DEFAULT 0
);
```
- Put this mod on your server, [reference](https://wiki.vintagestory.at/Adding_mods)
- Setup [PTE Database Server](https://github.com/Play-To-Earn-Currency/pte_databaseserver)
- Start the server once to generate configurations
- Go to DataPath from your vintage story server, find ModConfig and change the PlayToEarn necessary configurations
- Restart the server and everthing should be running

# Building
- Install .NET in your system, open terminal type: ``dotnet new install VintageStory.Mod.Templates``
- Create a template with the name ``PlayToEarn``: ``dotnet new vsmod --AddSolutionFile -o PlayToEarn``
- [Clone the repository](https://github.com/Play-To-Earn-Currency/vintage_story/archive/refs/heads/main.zip)
- Copy the ``CakeBuild`` and ``build.ps1`` or ``build.sh`` and paste inside the repository

Now you can build using the ``build.ps1`` or ``build.sh`` file
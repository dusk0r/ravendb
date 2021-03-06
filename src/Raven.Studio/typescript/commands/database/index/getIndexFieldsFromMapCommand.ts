import commandBase = require("commands/commandBase");
import database = require("models/resources/database");
import endpoints = require("endpoints");

class getIndexFieldsFromMapCommand extends commandBase {

    constructor(private db: database, private map: string) {
        super();
    }

    execute(): JQueryPromise<resultsDto<string>> {
        const url = endpoints.databases.studioIndex.studioIndexFields;
        const args = {
            Map: this.map
        };
        return this.post(url, JSON.stringify(args), this.db);
    }
} 

export = getIndexFieldsFromMapCommand;
